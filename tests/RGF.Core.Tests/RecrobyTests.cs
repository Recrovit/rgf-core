using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Recrovit.AI.Core;
using Recrovit.AI.Core.Conversations;
using Recrovit.AI.Core.Conversations.History;
using Recrovit.AI.Core.DependencyInjection;
using Recrovit.AI.Core.Workflows;
using Recrovit.AI.Runtime;
using Recrovit.AI.Runtime.Configuration;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Identity;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Workflows;
using Xunit;

namespace RGF.Core.Tests;

#pragma warning disable MAAI001 // Verify the same routing API used by Recrovit.AI.

[Collection("Recroby dictionary")]
public sealed class RecrobyTests
{
    [Theory]
    [InlineData(ContinuationResponseMode.FreeText, "free text", "free text")]
    [InlineData(ContinuationResponseMode.ChoiceOrText, "approve", "private agent instruction")]
    [InlineData(ContinuationResponseMode.ChoiceOrText, "  custom answer  ", "  custom answer  ")]
    [InlineData(ContinuationResponseMode.ChoiceOnly, "approve", "private agent instruction")]
    public async Task RuntimeMapsContinuationModesAndKeepsAgentTextOnServer(
        ContinuationResponseMode mode, string input, string expected)
    {
        using var dictionary = new RecrobyDictionaryFixture();
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, responseMode: mode);
        fixture.Credit.Charge = 1m;
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(first.Success);
        Assert.Equal(mode.ToString(), first.ResponseMode.ToString());
        if (mode == ContinuationResponseMode.FreeText)
        {
            Assert.Empty(first.Choices!);
            Assert.Empty(((TestIdentityProxy)fixture.Identity).LanguageRequests);
        }
        else
        {
            var choice = Assert.Single(first.Choices!);
            Assert.Equal("approve", choice.Code);
            Assert.Equal("Original choice", choice.UserText);
        }
        var json = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("LlmText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private agent instruction", json);
        var completed = await fixture.Service.ExecuteAsync("owner", Reply(first, input), Token);
        Assert.True(completed.Success);
        Assert.Equal(expected, fixture.Decision.Inputs.Last());
        Assert.Null(completed.ResponseMode);
        Assert.Null(completed.Choices);
        Assert.Equal(first.WorkflowRunId, completed.WorkflowRunId);
        Assert.Equal(1, fixture.Credit.UsageCalls);
        Assert.Equal(1m, completed.CreditUsage!.ChargedCredit);
        Assert.Single(fixture.DefaultChat.Messages);
    }

    [Fact]
    public async Task InvalidChoiceCanBeCorrectedWithOriginalTokenWithoutConsumingPendingRun()
    {
        using var dictionary = new RecrobyDictionaryFixture();
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, responseMode: ContinuationResponseMode.ChoiceOnly);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        var invalid = await fixture.Service.ExecuteAsync("owner", Reply(first, "invalid"), Token);
        Assert.False(invalid.Success);
        Assert.Equal("InvalidContinuationResponse", invalid.ErrorCode);
        Assert.Equal("WaitingForInput", invalid.WorkflowStatus);
        Assert.Equal(first.ConversationId, invalid.ConversationId);
        Assert.Equal(first.WorkflowRunId, invalid.WorkflowRunId);
        Assert.Equal(first.ConversationToken, invalid.ConversationToken);
        Assert.NotEmpty(invalid.Message);
        Assert.Empty(fixture.Credit.Users);
        Assert.Single(fixture.Decision.Contexts);
        fixture.Extension!.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("owner", Reply(invalid, "approve"), Token));
        fixture.Extension.Allowed = true;
        var completed = await fixture.Service.ExecuteAsync("owner", Reply(invalid, "approve"), Token);
        Assert.True(completed.Success);
        Assert.Equal("Completed", completed.WorkflowStatus);
        Assert.Equal(first.WorkflowRunId, completed.WorkflowRunId);
        Assert.Equal(1, fixture.Extension.Prepared);
        Assert.Equal("private agent instruction", fixture.Decision.Inputs.Last());
        Assert.Single(fixture.Credit.Users);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoicesUseStoredUserLanguageAndDictionaryFallbacksAlsoInBackground(bool background)
    {
        using var dictionary = new RecrobyDictionaryFixture();
        dictionary.Add("eng", "RGF.Recroby.Continuation.host.workflow.host.ask", "approve", "Approve");
        dictionary.Add("hun", "RGF.Recroby.Continuation.host.workflow.host.ask", "approve", "Jóváhagyás");
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.ChoiceOnly);
        var identity = (TestIdentityProxy)fixture.Identity;
        identity.UsePrincipalUserId = true;
        identity.Languages["alice"] = "hun";
        identity.Languages["bob"] = "eng";
        identity.Languages["missing"] = " ";
        foreach (var (user, expected) in new[] { ("alice", "Jóváhagyás"), ("bob", "Approve"), ("missing", "Approve") })
        {
            RgfAiResponse response = null!;
            var request = new RgfAiRequest { CurrentUserMessage = "first", CustomParams = new() { ["language"] = "attacker" } };
            if (background)
            {
                var captured = await fixture.Capture(user);
                fixture.Http.HttpContext = null;
                await captured.RunAsync(async ct => response = await fixture.Service.ExecuteAsync(user, request, new HostContext(user, 42), ct), Token);
            }
            else
            {
                fixture.Authenticate(user);
                response = await fixture.Service.ExecuteAsync(user, request, new HostContext(user, 42), Token);
            }
            Assert.True(response.Success);
            Assert.Equal(expected, Assert.Single(response.Choices!).UserText);
            Assert.Equal("approve", Assert.Single(response.Choices!).Code);
        }
        Assert.Equal(new[] { "alice", "bob", "missing" }, identity.LanguageRequests);
        Assert.Empty(fixture.Credit.Users);
    }

    [Fact]
    public async Task MissingUserTranslationUsesDictionaryDefaultBeforeOriginalChoice()
    {
        using var dictionary = new RecrobyDictionaryFixture();
        dictionary.Add("eng", "RGF.Recroby.Continuation.host.workflow.host.ask", "approve", "Default translation");
        dictionary.Add("hun", "RGF.Recroby.Continuation.host.workflow.host.ask", "unused", "Unused");
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.ChoiceOnly);
        ((TestIdentityProxy)fixture.Identity).Languages["owner"] = "hun";
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(first.Success);
        Assert.Equal("Default translation", Assert.Single(first.Choices!).UserText);
    }

    [Fact]
    public async Task TranslationFailurePreservesWaitingIdentityOtherChoicesAndRuntimeResume()
    {
        using var dictionary = new RecrobyDictionaryFixture();
        const string scope = "RGF.Recroby.Continuation.host.workflow.host.ask";
        dictionary.Add("eng", scope, "before", "Translated before");
        dictionary.Add("eng", scope, "after", "Translated after");
        var failure = new InvalidOperationException("Translation lookup failed");
        dictionary.FailTranslation("eng", scope, "approve", failure);
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, responseMode: ContinuationResponseMode.ChoiceOnly,
            choices: [
                new() { Code = "before", UserText = "Original before", LlmText = "before instruction" },
                new() { Code = "approve", UserText = "Original choice", LlmText = "private agent instruction" },
                new() { Code = "after", UserText = "Original after", LlmText = "after instruction" },
                new() { Code = "missing", UserText = "Original missing", LlmText = "missing instruction" }
            ]);
        var waiting = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(waiting.Success);
        Assert.Equal("WaitingForInput", waiting.WorkflowStatus);
        Assert.Null(waiting.ErrorCode);
        Assert.NotEmpty(waiting.ConversationToken!);
        Assert.NotEmpty(waiting.ConversationId);
        Assert.NotEmpty(waiting.WorkflowRunId!);
        Assert.Equal(new[] { ("before", "Translated before"), ("approve", "Original choice"),
            ("after", "Translated after"), ("missing", "Original missing") },
            waiting.Choices!.Select(choice => (choice.Code, choice.UserText)));
        var warning = Assert.Single(fixture.Logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("host.workflow", warning.State["WorkflowId"]);
        Assert.Equal("host.ask", warning.State["AgentId"]);
        Assert.Equal("approve", warning.State["Code"]);
        var protectedIdentity = JsonSerializer.Deserialize<JsonElement>(fixture.Protection
            .CreateProtector("RGF.Recroby.ConversationIdentity.v1").Unprotect(waiting.ConversationToken!));
        Assert.Equal(waiting.ConversationId, protectedIdentity.GetProperty("ConversationId").GetString());
        Assert.Equal(waiting.WorkflowRunId, protectedIdentity.GetProperty("RunId").GetString());
        Assert.NotEmpty(protectedIdentity.GetProperty("RequestId").GetString()!);
        var completed = await fixture.Service.ExecuteAsync("owner", Reply(waiting, "approve"), Token);
        Assert.True(completed.Success);
        Assert.Equal("Completed", completed.WorkflowStatus);
        Assert.Equal(waiting.ConversationId, completed.ConversationId);
        Assert.Equal(waiting.WorkflowRunId, completed.WorkflowRunId);
        Assert.Equal("private agent instruction", fixture.Decision.Inputs.Last());
        Assert.Equal(1, fixture.Extension!.Prepared);
        Assert.Single(fixture.DefaultChat.Messages);
    }

    [Fact]
    public async Task UserLanguageLookupFailureUsesDefaultTranslationsAndPreservesPendingRuntimeIdentity()
    {
        using var dictionary = new RecrobyDictionaryFixture();
        dictionary.Add("eng", "RGF.Recroby.Continuation.host.workflow.host.ask", "approve", "Default translation");
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, responseMode: ContinuationResponseMode.ChoiceOnly,
            choices: [
                new() { Code = "approve", UserText = "Original choice", LlmText = "private agent instruction" },
                new() { Code = "missing", UserText = "Original missing", LlmText = "missing instruction" }
            ]);
        var identity = (TestIdentityProxy)fixture.Identity;
        var failure = new System.Data.DataException("User query failed");
        identity.UserLookupFailure = failure;
        var waiting = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(waiting.Success);
        Assert.Null(waiting.ErrorCode);
        Assert.Equal("WaitingForInput", waiting.WorkflowStatus);
        Assert.Equal(RgfAiResponseMode.ChoiceOnly, waiting.ResponseMode);
        Assert.Equal(new[] { ("approve", "Default translation"), ("missing", "Original missing") },
            waiting.Choices!.Select(choice => (choice.Code, choice.UserText)));
        Assert.NotEmpty(waiting.ConversationToken!);
        var protectedIdentity = JsonSerializer.Deserialize<JsonElement>(fixture.Protection
            .CreateProtector("RGF.Recroby.ConversationIdentity.v1").Unprotect(waiting.ConversationToken!));
        Assert.Equal(waiting.ConversationId, protectedIdentity.GetProperty("ConversationId").GetString());
        Assert.Equal(waiting.WorkflowRunId, protectedIdentity.GetProperty("RunId").GetString());
        Assert.NotEmpty(protectedIdentity.GetProperty("RequestId").GetString()!);
        var warning = Assert.Single(fixture.Logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("owner", warning.State["UserId"]);
        Assert.Equal("host.workflow", warning.State["WorkflowId"]);
        Assert.Equal(Recrovit.RecroGridFramework.RecroDict.DefaultLanguage, warning.State["Language"]);
        Assert.Equal("owner", Assert.Single(identity.LanguageRequests));
        var completed = await fixture.Service.ExecuteAsync("owner", Reply(waiting, "approve"), Token);
        Assert.True(completed.Success);
        Assert.Equal("Completed", completed.WorkflowStatus);
        Assert.Equal(waiting.ConversationId, completed.ConversationId);
        Assert.Equal(waiting.WorkflowRunId, completed.WorkflowRunId);
        Assert.Equal("private agent instruction", fixture.Decision.Inputs.Last());
        Assert.Equal(1, fixture.Extension!.Prepared);
        Assert.Single(fixture.DefaultChat.Messages);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UserLanguageLookupCancellationPropagatesWithoutFallback(bool cancelToken, bool otherException)
    {
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.ChoiceOnly);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var identity = (TestIdentityProxy)fixture.Identity;
        identity.UserLookupFailure = otherException ? new System.Data.DataException("User query failed during cancellation")
            : new OperationCanceledException(cancellation.Token);
        if (cancelToken) identity.OnUserLookup = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ExecuteAsync("owner",
            new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), cancellation.Token));
        Assert.DoesNotContain(fixture.Logger.Entries, entry => ReferenceEquals(entry.Exception, identity.UserLookupFailure));
        Assert.Single(fixture.Decision.Contexts);
    }

    [Fact]
    public async Task UserLanguageLookupAuthorizationFailurePropagatesWithoutFallback()
    {
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.ChoiceOnly);
        var failure = new UnauthorizedAccessException("User lookup is not authorized");
        ((TestIdentityProxy)fixture.Identity).UserLookupFailure = failure;
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("owner",
            new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token));
        Assert.Same(failure, exception);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
    }

    [Fact]
    public async Task FreeTextContinuationDoesNotQueryUserLanguageEvenWhenLookupWouldFail()
    {
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.FreeText);
        var identity = (TestIdentityProxy)fixture.Identity;
        identity.UserLookupFailure = new System.Data.DataException("User query must not run");
        var waiting = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(waiting.Success);
        Assert.Equal("WaitingForInput", waiting.WorkflowStatus);
        Assert.Equal(RgfAiResponseMode.FreeText, waiting.ResponseMode);
        Assert.Empty(waiting.Choices!);
        Assert.Empty(identity.LanguageRequests);
        Assert.True((await fixture.Service.ExecuteAsync("owner", Reply(waiting, "free answer"), Token)).Success);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TranslationCancellationPropagatesWithoutFallback(bool cancelToken, bool otherException)
    {
        using var dictionary = new RecrobyDictionaryFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Exception failure = otherException ? new InvalidOperationException("Lookup failed during cancellation")
            : new OperationCanceledException(cancellation.Token);
        dictionary.FailTranslation("eng", "RGF.Recroby.Continuation.host.workflow.host.ask", "approve", failure,
            cancelToken ? cancellation.Cancel : null);
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.ChoiceOnly);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ExecuteAsync("owner",
            new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), cancellation.Token));
        Assert.DoesNotContain(fixture.Logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
        Assert.Single(fixture.Decision.Contexts);
    }

    [Fact]
    public async Task StructuredContinuationRetainsItsContractAndDoesNotResolveChoiceLanguage()
    {
        await using var fixture = new Fixture(custom: true, responseMode: ContinuationResponseMode.ChoiceOnly, structured: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(first.Success);
        Assert.Equal(RgfAiResponseMode.FreeText, first.ResponseMode);
        Assert.Empty(first.Choices!);
        Assert.Empty(((TestIdentityProxy)fixture.Identity).LanguageRequests);
        var invalid = await fixture.Service.ExecuteAsync("owner", Reply(first, "approve"), Token);
        Assert.False(invalid.Success);
        Assert.Equal("InvalidContinuationResponse", invalid.ErrorCode);
        Assert.Equal(first.ConversationToken, invalid.ConversationToken);
        Assert.Single(fixture.Decision.Contexts);
        var protectedIdentity = JsonSerializer.Deserialize<JsonElement>(fixture.Protection
            .CreateProtector("RGF.Recroby.ConversationIdentity.v1").Unprotect(first.ConversationToken!));
        var completed = await fixture.Workflows.ResumeWorkflowAsync(first.WorkflowRunId!,
            protectedIdentity.GetProperty("RequestId").GetString()!, new()
            {
                Value = new() { ContractId = "host.input", Value = JsonSerializer.SerializeToElement("structured answer") }
            }, Token);
        Assert.Equal(WorkflowStatus.Completed, completed.Status);
        Assert.Equal("structured answer", completed.FinalUserMessage);
    }

    [Fact]
    public async Task EveryModelCallChecksCreditOnceInFreshScopeAndOtherGuardsStillRun()
    {
        await using var fixture = new Fixture();
        fixture.Credit.Charge = 0.5m;
        var response = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "Hello" }, Token);
        Assert.True(response.Success);
        Assert.Equal(1m, response.CreditUsage!.ChargedCredit);
        Assert.Equal(2, response.CreditUsage.LlmCallCount);
        Assert.Equal(1m, Assert.Single(response.CreditUsage.Models).ChargedCredit);
        Assert.Equal(new[] { "intent", "chat" }, fixture.DefaultChat.Invocations);
        Assert.Equal(new[] { "owner", "owner" }, fixture.Credit.Users);
        Assert.Equal(2, fixture.GuardContexts.Count);
        Assert.NotSame(fixture.Credit.Instances[0], fixture.Credit.Instances[1]);
        Assert.All(fixture.Credit.Instances, credit => Assert.True(credit.Disposed));
        Assert.Equal(2, fixture.Credit.UsageCalls);
        Assert.Equal(2, fixture.Credit.Usages.Select(call => call.Usage.RequestId).Distinct().Count());
        Assert.Equal(2, fixture.UsageContexts.Count);
        Assert.All(fixture.Credit.Usages, call =>
        {
            Assert.Equal("owner", call.UserId);
            Assert.Equal("p", call.Usage.ProviderId);
            Assert.Equal("default", call.Usage.ModelId);
            Assert.Equal((11L, 12L, 23L, 4L, 5L), (call.Usage.InputTokens, call.Usage.OutputTokens,
                call.Usage.TotalTokens, call.Usage.CachedInputTokens, call.Usage.ReasoningTokens));
            var observed = Assert.Single(fixture.UsageContexts, context => context.RequestId == call.Usage.RequestId);
            Assert.Equal("native-model", observed.ModelId);
            Assert.Equal(call.Usage.ModelId, Assert.Single(response.CreditUsage.Models).ModelId);
            Assert.Equal(observed.ExecutionRoute, call.Usage.ExecutionRoute);
            Assert.Contains(fixture.GuardContexts, context => ReferenceEquals(context, observed.RequestContext));
        });
    }

    [Theory]
    [InlineData(AiCreditDenialReason.UserDisabled, "Test User", "Test User", false)]
    [InlineData(AiCreditDenialReason.InsufficientCredit, "Test User", "Test User", false)]
    [InlineData(AiCreditDenialReason.BalanceExpired, "Test User", "Test User", false)]
    [InlineData(AiCreditDenialReason.InvalidConfiguration, "Test User", "Test User", false)]
    [InlineData(AiCreditDenialReason.UserNotFound, "Test User", "Test User", false)]
    [InlineData(AiCreditDenialReason.InfrastructureFailure, "Test User", "Test User", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, "  Kovács János  ", "Kovács János", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, "<script>alert('name')</script>", "<script>alert('name')</script>", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, null, "this user", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, " ", "this user", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, "Name\nInjected", "this user", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, "Name\u202eInjected", "this user", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, "NNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNN", "this user", false)]
    [InlineData(AiCreditDenialReason.UserDisabled, "Test User", "this user", true)]
    public async Task SecondModelDenialStopsProviderAndReturnsStructuredCreditFailure(
        AiCreditDenialReason reason, string? userName, string expectedName, bool failCredentials)
    {
        await using var fixture = new Fixture();
        var identity = (TestIdentityProxy)fixture.Identity;
        identity.UserName = userName;
        identity.FailCredentials = failCredentials;
        fixture.Credit.Decisions.Enqueue(null);
        fixture.Credit.Decisions.Enqueue(reason);
        var response = await fixture.Service.ExecuteAsync("owner", new()
        {
            CurrentUserMessage = "Hello", CustomParams = new() { ["userName"] = "Spoofed client name" }
        }, Token);
        Assert.False(response.Success);
        Assert.Equal($"AiCredit.{reason}", response.ErrorCode);
        Assert.NotEmpty(response.Message);
        if (reason == AiCreditDenialReason.UserDisabled)
            Assert.Equal($"AI Credit access is disabled for {expectedName}.", response.Message);
        Assert.DoesNotContain("Spoofed client name", response.Message);
        Assert.DoesNotContain("private identity detail", response.Message);
        Assert.Null(response.WorkflowStatus);
        Assert.Equal("intent", Assert.Single(fixture.DefaultChat.Invocations));
        Assert.Equal(2, fixture.Credit.Users.Count);
        Assert.Equal("owner", Assert.Single(fixture.Credit.Usages).UserId);
        var conversation = fixture.GuardContexts[0].ConversationId!;
        Assert.DoesNotContain(fixture.History.Read(conversation), message => message.Role == ConversationHistoryRole.Assistant);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingHttpOrUserIdentityStopsProvider(bool httpContext, bool anonymous)
    {
        await using var fixture = new Fixture();
        if (!httpContext) fixture.Http.HttpContext = null;
        else if (anonymous) fixture.Http.HttpContext = new DefaultHttpContext();
        else ((TestIdentityProxy)fixture.Identity).UserId = " ";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("owner", new()
        { CurrentUserMessage = "Hello", CustomParams = new() { ["userId"] = "owner" } }, Token));
        Assert.Empty(fixture.Credit.Users);
        Assert.Empty(fixture.DefaultChat.Invocations);
        Assert.Empty(fixture.Credit.Usages);
    }

    [Fact]
    public async Task ResumeChecksCreditBeforeItsFirstModelCallAndPreservesConversationIdentityOnDenial()
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True(first.Success);
        Assert.Empty(fixture.Credit.Users);
        fixture.Credit.Decisions.Enqueue(AiCreditDenialReason.InsufficientCredit);
        var denied = await fixture.Service.ExecuteAsync("owner", Reply(first, "continue"), Token);
        Assert.False(denied.Success);
        Assert.Equal("AiCredit.InsufficientCredit", denied.ErrorCode);
        Assert.Equal(first.ConversationId, denied.ConversationId);
        Assert.Equal(first.ConversationToken, denied.ConversationToken);
        Assert.Equal(first.WorkflowRunId, denied.WorkflowRunId);
        Assert.Null(denied.WorkflowStatus);
        Assert.Equal("owner", Assert.Single(fixture.Credit.Users));
        Assert.Empty(fixture.DefaultChat.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCallingChecksEveryAdditionalModelRequest(bool denySecond)
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, toolCalling: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        fixture.Http.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "owner")], "test"));
        ((TestIdentityProxy)fixture.Identity).UsePrincipalUserId = true;
        fixture.Credit.Decisions.Enqueue(null);
        fixture.Credit.Decisions.Enqueue(denySecond ? AiCreditDenialReason.InsufficientCredit : null);
        var response = await fixture.Service.ExecuteAsync("owner", Reply(first, "continue"), Token);
        Assert.Equal(!denySecond, response.Success);
        Assert.Equal(denySecond ? "AiCredit.InsufficientCredit" : null, response.ErrorCode);
        Assert.Equal(2, fixture.Credit.Users.Count);
        Assert.Equal(denySecond ? 1 : 2, fixture.DefaultChat.Messages.Count);
        Assert.Equal(1, fixture.ToolInvocations);
        Assert.Equal(denySecond ? 1 : 2, fixture.Credit.UsageCalls);
        Assert.All(fixture.Credit.Usages, call => Assert.Equal("owner", call.UserId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingUsesScopedCreditGuardBeforeProvider(bool deny)
    {
        await using var fixture = new Fixture();
        fixture.Credit.Decisions.Enqueue(deny ? AiCreditDenialReason.UserDisabled : null);
        async Task Execute()
        {
            await foreach (var _ in fixture.Conversations.ExecuteStreamingAsync(null, "Hello", Token)) { }
        }
        if (deny)
        {
            var exception = await Assert.ThrowsAsync<AIProviderRequestRejectedException>(Execute);
            Assert.Equal("AiCredit.UserDisabled", exception.ReasonCode);
        }
        else await Execute();
        Assert.Equal(deny ? 0 : 1, fixture.DefaultChat.StreamingCalls);
        Assert.Single(fixture.Credit.Users);
        Assert.True(Assert.Single(fixture.Credit.Instances).Disposed);
        Assert.Equal(deny ? 0 : 1, fixture.Credit.UsageCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCreditCheckPropagatesAndStopsProvider(bool streaming)
    {
        await using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        fixture.Credit.OnCheck = token =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (streaming)
                await foreach (var _ in fixture.Conversations.ExecuteStreamingAsync(null, "Hello", cancellation.Token)) { }
            else await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "Hello" }, cancellation.Token);
        });
        Assert.Empty(fixture.DefaultChat.Invocations);
        Assert.Equal(0, fixture.DefaultChat.StreamingCalls);
    }

    [Fact]
    public void MissingCreditServiceFailsDependencyValidation()
    {
        var exception = Assert.Throws<AggregateException>(() => new Fixture(registerCredit: false));
        Assert.Contains(nameof(IAiCreditService), exception.ToString());
    }

    [Fact]
    public async Task ProviderWithoutUsageDoesNotApplyCredit()
    {
        await using var fixture = new Fixture();
        fixture.DefaultChat.Usage = null;
        Assert.True((await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "Hello" }, Token)).Success);
        Assert.Empty(fixture.Credit.Usages);
        Assert.Equal(2, fixture.UsageContexts.Count);
    }

    [Theory]
    [InlineData("early")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("complete")]
    public async Task StreamingPreservesKnownUsageAtEveryExit(string exit)
    {
        await using var fixture = new Fixture();
        fixture.DefaultChat.Usage = new() { InputTokenCount = 0, CachedInputTokenCount = 0 };
        fixture.DefaultChat.StreamFailure = exit switch
        {
            "failure" => new InvalidOperationException("provider failure"),
            "cancel" => new OperationCanceledException(),
            _ => null
        };
        async Task Execute()
        {
            await foreach (var update in fixture.Conversations.ExecuteStreamingAsync(null, "Hello", Token))
                if (exit == "early" && fixture.DefaultChat.StreamingCalls > 0) break;
        }
        if (exit == "failure") await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        else if (exit == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
        else await Execute();
        var call = Assert.Single(fixture.Credit.Usages);
        Assert.Equal("owner", call.UserId);
        Assert.Equal(0, call.Usage.InputTokens);
        Assert.Equal(0, call.Usage.CachedInputTokens);
        Assert.Null(call.Usage.OutputTokens);
        Assert.Null(call.Usage.TotalTokens);
        Assert.Null(call.Usage.ReasoningTokens);
        Assert.Equal(exit switch
        {
            "early" => AIProviderUsageStatus.Incomplete,
            "failure" => AIProviderUsageStatus.Failed,
            "cancel" => AIProviderUsageStatus.Cancelled,
            _ => AIProviderUsageStatus.Completed
        }, Assert.Single(fixture.UsageContexts).Status);
    }

    [Fact]
    public async Task ConcurrentModelCallsAccountToTheirAuthenticatedHttpUsers()
    {
        await using var fixture = new Fixture();
        ((TestIdentityProxy)fixture.Identity).UsePrincipalUserId = true;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.DefaultChat.OnStreamStart = async () =>
        {
            if (Interlocked.Increment(ref started) == 2) ready.SetResult();
            await ready.Task.WaitAsync(Token);
        };
        await Task.WhenAll(new[] { "alice", "bob" }.Select(async userId =>
        {
            fixture.Authenticate(userId);
            await foreach (var _ in fixture.Conversations.ExecuteStreamingAsync(null, "Hello", Token)) { }
        }));
        Assert.Equal(new[] { "alice", "bob" }, fixture.Credit.Usages.Select(call => call.UserId).Order().ToArray());
        Assert.Equal(2, fixture.Credit.Usages.Select(call => call.Usage.RequestId).Distinct().Count());
        Assert.Equal(2, fixture.UsageContexts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreditHookFailurePreservesOtherHandlersAndProviderFailure(bool providerFails)
    {
        await using var fixture = new Fixture();
        fixture.Credit.OnUsage = _ => throw new InvalidOperationException("accounting failure");
        fixture.DefaultChat.StreamFailure = providerFails ? new InvalidOperationException("provider failure") : null;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in fixture.Conversations.ExecuteStreamingAsync(null, "Hello", Token)) { }
        });
        Assert.Equal(providerFails ? "provider failure" : "accounting failure", exception.Message);
        if (providerFails)
            Assert.Equal("accounting failure", Assert.IsType<InvalidOperationException>(exception.Data["Recrovit.AI.ProviderUsageHandlerFailure"]).Message);
        Assert.Single(fixture.Credit.Usages);
        Assert.Single(fixture.UsageContexts);
    }

    [Fact]
    public async Task ExactlyOneMatchingExtensionExecutesDespiteManipulatedCustomParams()
    {
        await using var fixture = new Fixture(custom: true, multiple: true);
        var first = await fixture.Service.ExecuteAsync("owner", new()
        {
            CurrentUserMessage = "first",
            CustomParams = new() { ["workflowId"] = "other.workflow", ["hostContext"] = new OtherContext("owner") }
        }, new HostContext("owner", 42), Token);
        Assert.True(first.Success);
        Assert.Equal("WaitingForInput", first.WorkflowStatus);
        Assert.Equal(42, Assert.IsType<HostContext>(Assert.Single(fixture.Decision.Contexts)).Selection);
        Assert.Equal(1, fixture.Extension!.Validated);
        Assert.Equal(1, fixture.Extension.Prepared);
        Assert.Equal(0, fixture.OtherExtension!.Validated);
        Assert.Equal(0, fixture.OtherExtension.Prepared);
        var completed = await fixture.Service.ExecuteAsync("owner", Reply(first, "detail"), Token);
        Assert.True(completed.Success);
        Assert.Equal("Completed", completed.WorkflowStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroOrMultipleMatchesAreRejectedBeforeAuthorizationAndPreparation(bool ambiguous)
    {
        await using var fixture = new Fixture(custom: true, multiple: true, ambiguous: ambiguous);
        IWorkflowHostContext context = ambiguous ? new HostContext("owner", 42) : new RgfRecrobyContext("owner");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(
            "owner", new() { CurrentUserMessage = "first" }, context, Token));
        Assert.Equal(0, fixture.Extension!.Validated);
        Assert.Equal(0, fixture.Extension.Prepared);
        Assert.Equal(0, fixture.OtherExtension!.Validated);
        Assert.Equal(0, fixture.OtherExtension.Prepared);
        Assert.Empty(fixture.Decision.Contexts);
        Assert.Empty(fixture.DefaultChat.Invocations);
    }

    [Fact]
    public async Task ProtectedWorkflowSelectsOriginalExtensionForPendingAndCompletedTurns()
    {
        await using var fixture = new Fixture(custom: true, multiple: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        var request = Reply(first, "detail");
        request.CustomParams = new() { ["workflowId"] = "other.workflow" };
        var completed = await fixture.Service.ExecuteAsync("owner", request, new OtherContext("owner"), Token);
        Assert.True(completed.Success);
        Assert.Equal("Completed", completed.WorkflowStatus);
        Assert.Same(fixture.Decision.Contexts[0], fixture.Decision.Contexts[1]);
        var next = await fixture.Service.ExecuteAsync("owner", Reply(completed, "next"), new OtherContext("owner"), Token);
        Assert.True(next.Success);
        Assert.NotEqual(completed.WorkflowRunId, next.WorkflowRunId);
        Assert.Equal(3, fixture.Extension!.Validated);
        Assert.Equal(2, fixture.Extension.Prepared);
        Assert.Equal(0, fixture.OtherExtension!.Validated);
        Assert.Equal(0, fixture.OtherExtension.Prepared);
    }

    [Fact]
    public async Task GenericConversationBypassesApplicationExtensionAndIgnoresCustomWorkflowSelection()
    {
        await using var fixture = new Fixture(custom: true, multiple: true);
        fixture.Extension!.Allowed = false;
        fixture.DefaultChat.DecisionKeys.Enqueue("chat");
        var first = await fixture.Service.ExecuteAsync("owner", new()
        {
            CurrentUserMessage = "Hello", CustomParams = new() { ["workflowId"] = "host.workflow" }
        }, Token);
        Assert.True(first.Success);
        fixture.DefaultChat.DecisionKeys.Enqueue("chat");
        var next = await fixture.Service.ExecuteAsync("owner", Reply(first, "Continue"), new HostContext("owner", 99), Token);
        Assert.True(next.Success);
        Assert.Equal(first.ConversationId, next.ConversationId);
        Assert.Equal(0, fixture.Extension.Prepared);
        Assert.Equal(0, fixture.Extension.Validated);
        Assert.Empty(fixture.Extension.Selections);
        Assert.Empty(fixture.OtherExtension!.Selections);
        Assert.Equal(0, fixture.OtherExtension.Validated);
        Assert.Equal(0, fixture.OtherExtension.Prepared);
        Assert.Empty(fixture.Decision.Contexts);
    }

    [Fact]
    public async Task DefaultWorkflowUsesNativeIntentDecisionAndTwoTerminalBranches()
    {
        await using var fixture = new Fixture();
        var workflow = fixture.DefaultWorkflow;
        Assert.Equal("rgf.recroby", workflow.Id);
        Assert.Equal("rgf.recroby.intent", workflow.EntryAgentId);
        var intent = Assert.Single(workflow.Agents, agent => agent.Id == workflow.EntryAgentId);
        Assert.Equal(AgentType.Decision, intent.Type);
        Assert.Equal(DecisionExecutionMode.AI, intent.DecisionExecutionMode);
        Assert.True(intent.IncludeConversationHistory);
        Assert.False(intent.AllowInputRequest);
        Assert.Equal(3, workflow.Agents.Count);
        Assert.Equal(2, workflow.Relations.Count);
        Assert.All(workflow.Relations, relation => Assert.Equal(intent.Id, relation.SourceAgentId));
        Assert.Equal(new[] { "application", "chat" }, workflow.Relations.Select(relation => relation.DecisionKey).Order());
        foreach (var key in new[] { "chat", "application" })
        {
            var relation = Assert.Single(workflow.Relations, relation => relation.DecisionKey == key);
            Assert.Equal($"rgf.recroby.{key}", relation.TargetAgentId);
            var terminal = Assert.Single(workflow.Agents, agent => agent.Id == relation.TargetAgentId);
            Assert.Equal(AgentType.Executor, terminal.Type);
            Assert.Equal(ExecutorExecutionMode.AI, terminal.ExecutorExecutionMode);
            Assert.False(terminal.AllowInputRequest);
            Assert.Equal("rgf.recroby.response", terminal.OutputContractId);
            Assert.Empty(terminal.AllowedToolIds);
            Assert.DoesNotContain(workflow.Relations, relation => relation.SourceAgentId == terminal.Id);
            if (key == "chat") Assert.True(terminal.IncludeConversationHistory);
        }
    }

    [Theory]
    [InlineData("chat", "default answer")]
    [InlineData("application", "Application-specific request handling is not implemented yet.")]
    public async Task IntentRoutesToSelectedTerminalResponse(string decisionKey, string expectedAnswer)
    {
        await using var fixture = new Fixture();
        fixture.DefaultChat.DecisionKeys.Enqueue(decisionKey);
        var response = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "routing input" }, Token);
        Assert.True(response.Success);
        Assert.Equal("Completed", response.WorkflowStatus);
        Assert.Equal(expectedAnswer, response.Message);
        Assert.Equal(new[] { "intent", decisionKey }, fixture.DefaultChat.Invocations);
    }

    [Fact]
    public async Task NewConversationAndCompletedTurnUseNativeHistoryAndIssueProtectedIdentity()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, Token);
        Assert.True(first.Success);
        Assert.Equal("Completed", first.WorkflowStatus);
        Assert.Equal("default answer", first.Message);
        Assert.NotEmpty(first.ConversationId);
        Assert.NotEmpty(first.ConversationToken!);
        Assert.NotEmpty(first.WorkflowRunId!);
        var next = await fixture.Service.ExecuteAsync("owner", Reply(first, "second"), Token);
        Assert.Equal(first.ConversationId, next.ConversationId);
        Assert.NotEqual(first.WorkflowRunId, next.WorkflowRunId);
        Assert.True(next.Success);
        Assert.Equal("Completed", next.WorkflowStatus);
        Assert.Equal("default answer", next.Message);
        Assert.Equal(new[] { "intent", "chat", "intent", "chat" }, fixture.DefaultChat.Invocations);
        foreach (var messages in fixture.DefaultChat.Messages.Skip(2))
        {
            Assert.Equal(new[]
            {
                (ChatRole.User, "first"),
                (ChatRole.Assistant, "default answer"),
                (ChatRole.User, "second")
            }, messages.Select(message => (message.Role, message.Text)));
        }
    }

    [Fact]
    public async Task CustomWorkflowResumesOriginalContextAndInputWithoutPreparingAnotherRun()
    {
        await using var fixture = new Fixture(custom: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("untrusted-owner", 42), Token);
        Assert.Equal("WaitingForInput", first.WorkflowStatus);
        Assert.True(first.Success);
        Assert.Equal("More detail?", first.Message);
        var original = Assert.Single(fixture.Decision.Contexts);
        Assert.Equal("owner", Assert.IsType<HostContext>(original).UserId);
        var next = await fixture.Service.ExecuteAsync("owner", Reply(first, "detail"), new HostContext("untrusted-owner", 99), Token);
        Assert.Equal("Completed", next.WorkflowStatus);
        Assert.Equal("detail", next.Message);
        Assert.Equal(first.ConversationId, next.ConversationId);
        Assert.Equal(first.WorkflowRunId, next.WorkflowRunId);
        Assert.Same(original, fixture.Decision.Contexts[1]);
        Assert.Equal(1, fixture.Extension!.Prepared);
        Assert.Equal(2, fixture.Extension.Validated);
        var validation = fixture.Extension.Validations[1];
        Assert.True(validation.IsContinuation);
        Assert.Equal(first.ConversationId, validation.ConversationId);
        Assert.Same(original, validation.WorkflowHostContext);
        Assert.Equal(42, Assert.IsType<HostContext>(validation.WorkflowHostContext).Selection);
        Assert.False(fixture.Extension.Validations[0].IsContinuation);
        Assert.Null(fixture.Extension.Validations[0].WorkflowHostContext);
        var third = await fixture.Service.ExecuteAsync("owner", Reply(next, "next run"), new HostContext("untrusted-owner", 99), Token);
        Assert.NotEqual(next.WorkflowRunId, third.WorkflowRunId);
        Assert.Equal(next.ConversationId, third.ConversationId);
        Assert.Equal(99, Assert.IsType<HostContext>(fixture.Decision.Contexts[2]).Selection);
        Assert.Equal(2, fixture.Extension.Prepared);
        Assert.Empty(fixture.DefaultChat.Messages);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("conversation")]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("missing-id")]
    public async Task InvalidIdentityCannotReachExtensionOrResume(string attack)
    {
        await using var fixture = new Fixture(custom: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        var request = Reply(first, "intrusion");
        var user = "owner";
        switch (attack)
        {
            case "user": user = "other"; break;
            case "conversation": request.ConversationId = "another"; break;
            case "missing": request.ConversationToken = null; break;
            case "tampered": request.ConversationToken = "modified-" + request.ConversationToken; break;
            case "missing-id": request.ConversationId = null; break;
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(user, request, Token));
        Assert.Single(fixture.Decision.Contexts);
        Assert.Equal(1, fixture.Extension!.Validated);
        var valid = await fixture.Service.ExecuteAsync("owner", Reply(first, "valid"), Token);
        Assert.True(valid.Success);
        Assert.Equal("valid", valid.Message);
    }

    [Fact]
    public async Task ReplayedContinuationIsRejectedByTheExistingRuntime()
    {
        await using var fixture = new Fixture(custom: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        Assert.True((await fixture.Service.ExecuteAsync("owner", Reply(first, "once"), Token)).Success);
        var replay = await fixture.Service.ExecuteAsync("owner", Reply(first, "twice"), Token);
        Assert.False(replay.Success);
        Assert.Equal("Failed", replay.WorkflowStatus);
        Assert.Equal(2, fixture.Decision.Contexts.Count);
    }

    [Theory]
    [InlineData("ConversationId")]
    [InlineData("WorkflowId")]
    [InlineData("RunId")]
    public async Task ProtectedIdentityMustMatchStoredRunBeforeApplicationValidation(string field)
    {
        await using var fixture = new Fixture(custom: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        var protector = fixture.Protection.CreateProtector("RGF.Recroby.ConversationIdentity.v1");
        var identity = System.Text.Json.Nodes.JsonNode.Parse(protector.Unprotect(first.ConversationToken!))!;
        identity[field] = "different";
        var request = Reply(first, "blocked");
        if (field == "ConversationId") request.ConversationId = "different";
        request.ConversationToken = protector.Protect(identity.ToJsonString());
        if (field == "RunId")
        {
            var error = await Assert.ThrowsAsync<WorkflowException>(() => fixture.Service.ExecuteAsync("owner", request, Token));
            Assert.Equal(WorkflowErrorCode.UnknownWorkflowRun, error.Code);
        }
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("owner", request, Token));
        Assert.Equal(1, fixture.Extension!.Validated);
        Assert.Single(fixture.Decision.Contexts);
        Assert.Empty(fixture.DefaultChat.Messages);
        Assert.True((await fixture.Service.ExecuteAsync("owner", Reply(first, "retry"), Token)).Success);
    }

    [Fact]
    public async Task ExtensionAuthorizationAlsoRunsBeforeResume()
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), Token);
        fixture.Extension!.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("owner", Reply(first, "blocked"), Token));
        Assert.Single(fixture.Decision.Contexts);
        Assert.Empty(fixture.DefaultChat.Messages);
        Assert.Empty(fixture.AlternateChat.Messages);
        Assert.Equal(1, fixture.Extension.Prepared);
        fixture.Extension.Allowed = true;
        var resumed = await fixture.Service.ExecuteAsync("owner", Reply(first, "retry"), Token);
        Assert.True(resumed.Success);
        Assert.Equal(first.WorkflowRunId, resumed.WorkflowRunId);
        Assert.Equal(2, fixture.Decision.Contexts.Count);
        Assert.Single(fixture.DefaultChat.Messages);
        Assert.Equal(1, fixture.Extension.Prepared);
    }

    [Fact]
    public async Task ModelAliasSelectsConfiguredProviderAndPersistsAcrossTurns()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first", AiModelOverride = "p/alternate" }, Token);
        Assert.True(first.Success);
        Assert.Equal("alternate answer", first.Message);
        var next = await fixture.Service.ExecuteAsync("owner", Reply(first, "second"), Token);
        Assert.Equal("alternate answer", next.Message);
        Assert.Equal(new[] { "intent", "chat", "intent", "chat" }, fixture.AlternateChat.Invocations);
        Assert.Empty(fixture.DefaultChat.Messages);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ExecuteAsync("owner",
            new() { CurrentUserMessage = "bad", AiModelOverride = "missing" }, Token));
        var defaultRequest = Reply(next, "return to default");
        defaultRequest.AiModelOverride = "p/default";
        var defaultResponse = await fixture.Service.ExecuteAsync("owner", defaultRequest, Token);
        Assert.True(defaultResponse.Success);
        Assert.Equal("default answer", defaultResponse.Message);
    }

    [Fact]
    public async Task PerRunModelOverrideIsRetainedUntilAIExecutesAfterResume()
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first", AiModelOverride = "alternate" }, new HostContext("owner", 42), Token);
        Assert.Equal("WaitingForInput", first.WorkflowStatus);
        Assert.Empty(fixture.AlternateChat.Messages);
        var next = await fixture.Service.ExecuteAsync("owner", Reply(first, "continue"), Token);
        Assert.True(next.Success);
        Assert.Equal("alternate answer", next.Message);
        Assert.Equal(first.WorkflowRunId, next.WorkflowRunId);
        Assert.Single(fixture.AlternateChat.Messages);
        Assert.Empty(fixture.DefaultChat.Messages);
    }

    [Theory]
    [InlineData(null, "high")]
    [InlineData("low", "low")]
    public async Task PendingRunRetainsOriginalReasoningDespiteContinuationOverride(string? effort, string expected)
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, reasoning: true);
        var first = await fixture.Service.ExecuteAsync("owner", new()
        { CurrentUserMessage = "first", AiModelOverride = "p/alternate", AiReasoningEffortOverride = effort },
            new HostContext("owner", 42), Token);
        var reply = Reply(first, "continue");
        reply.AiReasoningEffortOverride = "invalid-continuation-effort";
        var next = await fixture.Service.ExecuteAsync("owner", reply, Token);
        Assert.True(next.Success);
        Assert.Equal(first.WorkflowRunId, next.WorkflowRunId);
        Assert.Equal(expected, Assert.Single(fixture.AlternateChat.Reasoning)!.Effort);
        Assert.Empty(fixture.DefaultChat.Messages);
    }

    [Fact]
    public async Task PendingRunCannotChangeModelAndCancellationPropagates()
    {
        await using var fixture = new Fixture(custom: true);
        var first = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first", AiModelOverride = "alternate" }, new HostContext("owner", 42), Token);
        var request = Reply(first, "detail");
        request.AiModelOverride = "default";
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ExecuteAsync("owner", request, Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ExecuteAsync("owner", Reply(first, "cancel"), cancelled.Token));
        Assert.Single(fixture.Decision.Contexts);
        Assert.True((await fixture.Service.ExecuteAsync("owner", Reply(first, "valid"), Token)).Success);
    }

    [Fact]
    public void ExecutionSurfaceUsesOnlySharedRgfWireContracts()
    {
        var methods = typeof(RgfRecrobyService).GetMethods().Where(m => m.Name == "ExecuteAsync").ToArray();
        Assert.Equal(2, methods.Length);
        var method = Assert.Single(methods, m => m.GetParameters().Length == 3);
        Assert.Equal(typeof(Task<RgfAiResponse>), method.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(RgfAiRequest), typeof(CancellationToken) },
            method.GetParameters().Select(p => p.ParameterType));
        var trustedInvocation = Assert.Single(methods, m => m.GetParameters().Length == 4);
        Assert.Equal(typeof(Task<RgfAiResponse>), trustedInvocation.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(RgfAiRequest), typeof(IWorkflowHostContext), typeof(CancellationToken) },
            trustedInvocation.GetParameters().Select(p => p.ParameterType));
        foreach (var type in new[] { typeof(RgfAiRequest), typeof(RgfAiResponse), typeof(RgfAiMessage), typeof(RgfAiUsage) })
            Assert.DoesNotContain(type.GetProperties(), p => (p.PropertyType.FullName ?? "").Contains("Recrovit.AI")
                || (p.PropertyType.FullName ?? "").Contains("Microsoft.Agents"));
        Assert.DoesNotContain(typeof(RgfRecrobyService).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Schemora"));
    }

    [Theory]
    [InlineData(AiCreditMode.Unlimited)]
    [InlineData(AiCreditMode.Periodic)]
    [InlineData(AiCreditMode.Prepaid)]
    public async Task BackgroundWorkflowWaitsAndResumesWithFreshIdentityAndGuardedSettlement(AiCreditMode mode)
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true, toolCalling: true);
        fixture.Credit.Mode = mode;
        fixture.Credit.Charge = 2m;
        fixture.Credit.Prepaid = new(8m, null, false, DateTime.UtcNow);
        fixture.Credit.Periods = [new(AiCreditPeriodType.Daily, 10m, 2m, DateTime.UtcNow.AddDays(1), DateTime.UtcNow)];
        fixture.Credit.OnState = user => Assert.Equal("owner", user);
        var firstIdentity = await fixture.Capture();
        fixture.Http.HttpContext = null;
        RgfAiResponse first = null!;
        await firstIdentity.RunAsync(async ct => first = await fixture.Service.ExecuteAsync("owner",
            new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), ct), Token);
        Assert.Equal("WaitingForInput", first.WorkflowStatus);
        Assert.Equal("More detail?", first.Message);
        Assert.NotEmpty(first.ConversationToken!);
        Assert.Equal(mode, first.CreditState!.Mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstIdentity.RunAsync(_ => Task.CompletedTask, Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("owner", Reply(first, "closed"), Token));

        var secondIdentity = await fixture.Capture();
        fixture.Http.HttpContext = null;
        RgfAiResponse second = null!;
        await secondIdentity.RunAsync(async ct => second = await fixture.Service.ExecuteAsync("owner", Reply(first, "ask again"), ct), Token);
        Assert.Equal("WaitingForInput", second.WorkflowStatus);
        Assert.Equal(first.WorkflowRunId, second.WorkflowRunId);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(2, fixture.Extension!.Validated);
        Assert.Empty(fixture.Credit.Users);

        var thirdIdentity = await fixture.Capture();
        fixture.Http.HttpContext = null;
        RgfAiResponse third = null!;
        await thirdIdentity.RunAsync(async ct => third = await fixture.Service.ExecuteAsync("owner", Reply(second, "answer"), ct), Token);
        Assert.True(third.Success);
        Assert.Equal("Completed", third.WorkflowStatus);
        Assert.Equal(first.WorkflowRunId, third.WorkflowRunId);
        Assert.Equal(3, fixture.Extension.Validated);
        Assert.Equal(1, fixture.Extension.Prepared);
        Assert.Equal(new[] { "owner", "owner" }, fixture.Credit.Users);
        Assert.Equal(2, fixture.Credit.UsageCalls);
        Assert.All(fixture.Credit.Usages, call => Assert.Equal("owner", call.UserId));
        Assert.All(fixture.Credit.Instances, credit => Assert.True(credit.Disposed));
        Assert.Equal(4m, third.CreditUsage!.ChargedCredit);
        Assert.Equal(2, third.CreditUsage.LlmCallCount);
        Assert.Equal(mode, third.CreditState!.Mode);
        Assert.Equal(mode == AiCreditMode.Prepaid ? fixture.Credit.Prepaid : null, third.CreditState.Prepaid);
        Assert.Equal(mode == AiCreditMode.Periodic ? fixture.Credit.Periods : [], third.CreditState.Periods);
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("domain")]
    [InlineData("other-user")]
    [InlineData("wrong-facade-user")]
    public async Task BackgroundContinuationRechecksAuthorizationBeforeModelCalls(string denial)
    {
        await using var fixture = new Fixture(custom: true, aiAfterResume: true);
        ((TestIdentityProxy)fixture.Identity).UsePrincipalUserId = true;
        var firstIdentity = await fixture.Capture();
        fixture.Http.HttpContext = null;
        RgfAiResponse first = null!;
        await firstIdentity.RunAsync(async ct => first = await fixture.Service.ExecuteAsync("owner",
            new() { CurrentUserMessage = "first" }, new HostContext("owner", 42), ct), Token);
        fixture.Extension!.Allowed = denial != "domain";
        fixture.Credit.Decisions.Enqueue(AiCreditDenialReason.UserDisabled);
        var nextIdentity = await fixture.Capture(denial == "other-user" ? "other" : "owner");
        fixture.Http.HttpContext = null;
        await nextIdentity.RunAsync(async ct =>
        {
            var user = denial is "other-user" or "wrong-facade-user" ? "other" : "owner";
            if (denial == "credit")
            {
                var response = await fixture.Service.ExecuteAsync(user, Reply(first, "answer"), ct);
                Assert.Equal("AiCredit.UserDisabled", response.ErrorCode);
                Assert.Equal(first.ConversationToken, response.ConversationToken);
            }
            else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync(user, Reply(first, "answer"), ct));
        }, Token);
        Assert.Empty(fixture.DefaultChat.Messages);
        Assert.Empty(fixture.Credit.Usages);
        Assert.Equal(denial == "credit" ? 1 : 0, fixture.Credit.Users.Count);
        Assert.Equal(denial is "credit" or "domain" ? 2 : 1, fixture.Extension.Validated);
    }

    [Fact]
    public async Task HttpFacadeUserMismatchIsRejectedBeforeExtensionSelection()
    {
        await using var fixture = new Fixture(custom: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ExecuteAsync("other",
            new() { CurrentUserMessage = "first" }, new HostContext("other", 42), Token));
        Assert.Empty(fixture.Extension!.Selections);
        Assert.Empty(fixture.Credit.Users);
        Assert.Empty(fixture.DefaultChat.Messages);
    }
    [Theory]
    [InlineData(AiCreditDenialReason.UserDisabled)]
    [InlineData(AiCreditDenialReason.InsufficientCredit)]
    [InlineData(AiCreditDenialReason.BalanceExpired)]
    public async Task BackgroundModelCallsRecheckCreditAndUseCapturedValidatedName(AiCreditDenialReason reason)
    {
        await using var fixture = new Fixture();
        ((TestIdentityProxy)fixture.Identity).UserName = "  Captured User  ";
        var identity = await fixture.Capture();
        ((TestIdentityProxy)fixture.Identity).UserName = "Changed later";
        fixture.Http.HttpContext = null;
        fixture.Credit.Decisions.Enqueue(null);
        fixture.Credit.Decisions.Enqueue(reason);
        await identity.RunAsync(async ct =>
        {
            var response = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "hello" }, ct);
            Assert.Equal($"AiCredit.{reason}", response.ErrorCode);
            if (reason == AiCreditDenialReason.UserDisabled)
                Assert.Equal("AI Credit access is disabled for Captured User.", response.Message);
            Assert.Equal(1, response.CreditUsage!.LlmCallCount);
        }, Token);
        Assert.Equal(new[] { "owner", "owner" }, fixture.Credit.Users);
        Assert.Equal("owner", Assert.Single(fixture.Credit.Usages).UserId);
        Assert.Equal("intent", Assert.Single(fixture.DefaultChat.Invocations));
    }
    [Fact]
    public async Task EachRecrobyTurnRequiresANewCapturedExecutionIdentity()
    {
        await using var fixture = new Fixture(custom: true);
        var identity = await fixture.Capture();
        fixture.Http.HttpContext = null;
        await identity.RunAsync(async ct =>
        {
            var waiting = await fixture.Service.ExecuteAsync("owner", new() { CurrentUserMessage = "first" },
                new HostContext("owner", 42), ct);
            Assert.Equal("WaitingForInput", waiting.WorkflowStatus);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteAsync("owner", Reply(waiting, "answer"), ct));
            Assert.Equal(1, fixture.Extension!.Validated);
        }, Token);
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RgfAiRequest Reply(RgfAiResponse response, string text) => new()
    { ConversationId = response.ConversationId, ConversationToken = response.ConversationToken, CurrentUserMessage = text };

    private sealed record HostContext(string Owner, int Selection) : RgfRecrobyContext(Owner);
    private sealed record OtherContext(string Owner) : RgfRecrobyContext(Owner);
    private sealed record ToolValue(string Text);

    private class HostExtension(IStructuredContractRegistry contracts) : IRgfRecrobyExtension
    {
        public List<(string? WorkflowId, IWorkflowHostContext? Context)> Selections { get; } = [];
        public virtual bool CanHandle(string? workflowId, IWorkflowHostContext? hostContext)
        {
            Selections.Add((workflowId, hostContext));
            return workflowId is not null ? workflowId == "host.workflow" : hostContext is HostContext;
        }

        public int Prepared { get; private set; }
        public int Validated { get; private set; }
        public bool Allowed { get; set; } = true;
        public List<RgfRecrobyValidationContext> Validations { get; } = [];
        public ValueTask ValidateAsync(RgfRecrobyValidationContext context, RgfAiRequest request, CancellationToken token)
        {
            Validated++;
            Validations.Add(context);
            if (!Allowed) throw new UnauthorizedAccessException();
            return ValueTask.CompletedTask;
        }
        public ValueTask<RgfRecrobyExecution> PrepareAsync(RgfRecrobyContext context, RgfAiRequest request, CancellationToken token)
        {
            Prepared++;
            var selection = (context.InitialHostContext as HostContext)?.Selection ?? 42;
            return ValueTask.FromResult(new RgfRecrobyExecution("host.workflow", new HostContext(context.UserId, selection),
                [new AgentInputData { Source = AgentInputSource.Application, Role = "domain", Value = contracts.Serialize("host.input", "domain input") }]));
        }
    }

    private sealed class OtherExtension(IStructuredContractRegistry contracts) : HostExtension(contracts)
    {
        public bool Ambiguous { get; set; }
        public override bool CanHandle(string? workflowId, IWorkflowHostContext? hostContext)
        {
            Selections.Add((workflowId, hostContext));
            return Ambiguous
                ? workflowId is not null ? workflowId == "host.workflow" : hostContext is HostContext
                : workflowId is not null ? workflowId == "other.workflow" : hostContext is OtherContext;
        }
    }

    private sealed class AskOnce : IApplicationDecisionHandler
    {
        public bool Structured { get; set; }
        public List<string?> Inputs { get; } = [];
        public bool AiAfterResume { get; set; }
        public List<IWorkflowHostContext?> Contexts { get; } = [];
        public ValueTask<DefaultAgentOutput<DecisionResult>> ExecuteAsync(ApplicationDecisionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Contexts.Add(context.HostContext);
            Inputs.Add(context.Input.CurrentUserInput?.Text);
            var data = Assert.Single(context.Input.InputData);
            Assert.Equal("domain", data.Role);
            Assert.Equal("domain input", data.Value.Value.GetString());
            return ValueTask.FromResult(context.Input.CurrentUserInput is null || context.Input.CurrentUserInput.Text == "ask again"
                ? new DefaultAgentOutput<DecisionResult> { InputRequest = new AgentInputRequest
                    { Message = "More detail?", ExpectedContractId = Structured ? "host.input" : null } }
                : new DefaultAgentOutput<DecisionResult> { Output = new() { TargetAgentIds = AiAfterResume ? ["host.chat"] : [] },
                    UserMessage = context.Input.CurrentUserInput.Text ?? context.Input.CurrentUserInput.Value?.Value.GetString() });
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;
        public ChatClient DefaultChat { get; } = new("default answer");
        public ChatClient AlternateChat { get; } = new("alternate answer");
        public AskOnce Decision { get; } = new();
        public RgfRecrobyService Service { get; }
        public WorkflowDefinition DefaultWorkflow => provider.GetRequiredService<WorkflowRegistry>().Get("rgf.recroby");
        public HostExtension? Extension { get; }
        public OtherExtension? OtherExtension { get; }
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public AiCreditTestState Credit { get; } = new();
        public List<AIProviderRequestContext> GuardContexts { get; } = [];
        public List<AIProviderUsageContext> UsageContexts { get; } = [];
        public IHttpContextAccessor Http => provider.GetRequiredService<IHttpContextAccessor>();
        public IRgfIdentityService Identity => provider.GetRequiredService<IRgfIdentityService>();
        public IConversationHistoryReader History => provider.GetRequiredService<IConversationHistoryReader>();
        public IConversationExecutor Conversations => provider.GetRequiredService<IConversationExecutor>();
        public IWorkflowClient Workflows => provider.GetRequiredService<IWorkflowClient>();
        public int ToolInvocations { get; private set; }
        public RecordingRecrobyLogger Logger { get; } = new();
        public Fixture(bool custom = false, bool aiAfterResume = false, bool multiple = false, bool ambiguous = false, bool reasoning = false,
            bool toolCalling = false, bool registerCredit = true, ContinuationResponseMode? responseMode = null, bool structured = false,
            IReadOnlyList<ContinuationResponseChoice>? choices = null)
        {
            Decision.AiAfterResume = aiAfterResume;
            Decision.Structured = structured;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILogger<RgfRecrobyService>>(Logger);
            Credit.Register(services);
            services.AddScoped<IAIProviderRequestGuard>(_ => new RecordingGuard(GuardContexts));
            DefaultChat.CallTool = toolCalling;
            services.AddRecrovitAICore(configuration =>
            {
                configuration.AddDefaultRecrobyWorkflow();
                configuration.Contracts.Register<string>("host.input");
                configuration.Contracts.Register<ToolValue>("host.tool-value");
                configuration.Tools.Register(new() { Id = "host.tool", InputContractId = "host.tool-value", OutputContractId = "host.tool-value" },
                    (_, input, _) =>
                    {
                        ToolInvocations++;
                        return ValueTask.FromResult(new ApplicationToolResult { Output = input! });
                    });
                configuration.Decisions.Register("host.ask", Decision);
                configuration.Workflows.Register(new WorkflowDefinition
                {
                    Id = "host.workflow", Name = "Host workflow", EntryAgentId = "host.ask",
                    Agents = [new AgentDefinition { Id = "host.ask", Name = "Ask", Type = AgentType.Decision,
                        DecisionExecutionMode = DecisionExecutionMode.Application, AllowInputRequest = true,
                        ResponseConfiguration = responseMode is null ? null : new()
                        {
                            Mode = responseMode.Value,
                            Choices = responseMode == ContinuationResponseMode.FreeText ? [] : choices ??
                                [new() { Code = "approve", UserText = "Original choice", LlmText = "private agent instruction" }]
                        } },
                        new AgentDefinition { Id = "host.chat", Name = "Chat", Type = AgentType.Executor,
                            Instruction = "Respond", AllowInputRequest = false, OutputContractId = "rgf.recroby.response",
                            AllowedToolIds = toolCalling ? ["host.tool"] : [],
                            AIExecutionOptions = new() { ExecutionRoute = "p/default" } }],
                    Relations = [new AgentRelation { SourceAgentId = "host.ask", TargetAgentId = "host.chat", DecisionKey = "chat" }]
                });
            });
            services.AddRecrovitAIClient();
            services.AddSingleton<IChatClient>(new RoutePersistingRoutingChatClient(
                new Dictionary<string, IChatClient> { ["p/default"] = DefaultChat, ["p/alternate"] = AlternateChat },
                new RoutePersistingRoutingChatClientOptions { DefaultRoute = "p/default" }));
            services.AddSingleton(new AiRouteCatalog(new AiOptions
            {
                DefaultProvider = "p", Providers = new() { ["p"] = new()
                    { DefaultModel = "default", Models = new() { ["default"] = "native-default", ["alternate"] = "native-alternate" },
                        Reasoning = reasoning ? new() { Efforts = ["low", "high"], DefaultEffort = "high" } : null } }
            }));
            services.AddSingleton(Protection);
            services.AddSingleton<IAIProviderIdentityResolver>(provider => provider.GetRequiredService<AiRouteCatalog>());
            if (custom) services.AddRgfRecroby<HostExtension>(); else services.AddRgfRecroby();
            services.AddRgfRecroby();
            services.AddScoped<IAIProviderUsageHandler>(_ => new RecordingUsageHandler(UsageContexts));
            if (multiple) services.AddRgfRecroby<OtherExtension>();
            if (!registerCredit)
                for (var i = services.Count - 1; i >= 0; i--)
                    if (services[i].ServiceType == typeof(IAiCreditService)) services.RemoveAt(i);
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            scope = provider.CreateAsyncScope();
            AiCreditTestState.Authenticate(provider);
            Service = scope.ServiceProvider.GetRequiredService<RgfRecrobyService>();
            var extensions = scope.ServiceProvider.GetServices<IRgfRecrobyExtension>().ToArray();
            if (custom) Extension = Assert.Single(extensions.OfType<HostExtension>(), candidate => candidate.GetType() == typeof(HostExtension));
            if (multiple)
            {
                OtherExtension = Assert.Single(extensions.OfType<OtherExtension>());
                OtherExtension.Ambiguous = ambiguous;
            }
        }
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); await provider.DisposeAsync(); }
        public void Authenticate(string userId) => AiCreditTestState.Authenticate(provider, userId);
        public async Task<IRgfAiExecutionIdentity> Capture(string userId = "owner")
        {
            Authenticate(userId);
            await using var captureScope = provider.CreateAsyncScope();
            return await captureScope.ServiceProvider.GetRequiredService<IRgfAiExecutionIdentityFactory>().CaptureAsync(Token);
        }
    }

    private sealed class ChatClient(string answer) : IChatClient
    {
        public bool CallTool { get; set; }
        public UsageDetails? Usage { get; set; } = new()
        {
            InputTokenCount = 11, OutputTokenCount = 12, TotalTokenCount = 23,
            CachedInputTokenCount = 4, ReasoningTokenCount = 5
        };
        public Exception? StreamFailure { get; set; }
        public Func<Task>? OnStreamStart { get; set; }
        private int streamingCalls;
        public int StreamingCalls => streamingCalls;
        public List<ChatMessage[]> Messages { get; } = [];
        public List<string> Invocations { get; } = [];
        public List<AIReasoningOptions?> Reasoning { get; } = [];
        public Queue<string> DecisionKeys { get; } = new();
        public void Dispose() { }
        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(messages.ToArray());
            Reasoning.Add(AIReasoningChatOptions.Get(options));
            if (CallTool && Messages.Count == 1)
                return Result(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "host_tool", new Dictionary<string, object?> { ["text"] = "input" })]));
            var format = Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            var output = format.Schema!.Value.GetProperty("properties").GetProperty("output");
            if (output.TryGetProperty("properties", out var properties) && properties.TryGetProperty("decisionKey", out var decisionKeySchema))
            {
                Assert.Contains("Classify the user's current request.", options!.Instructions);
                var key = DecisionKeys.Count == 0 ? "chat" : DecisionKeys.Dequeue();
                Assert.Contains($"\"{key}\"", decisionKeySchema.GetRawText());
                Invocations.Add("intent");
                return Result(new ChatMessage(ChatRole.Assistant,
                    JsonSerializer.Serialize(new { userMessage = (string?)null, output = new { decisionKey = key } })));
            }
            Assert.Contains("string", output.GetProperty("type").EnumerateArray().Select(type => type.GetString()));
            var application = options!.Instructions?.Contains("application-specific request handling is not implemented yet.", StringComparison.Ordinal) == true;
            Invocations.Add(application ? "application" : "chat");
            var response = application ? "Application-specific request handling is not implemented yet." : answer;
            return Result(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(new { userMessage = response, output = response })));
        }
        private Task<ChatResponse> Result(ChatMessage message)
            => Task.FromResult(new ChatResponse(message) { Usage = Usage, ModelId = "native-model" });
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref streamingCalls);
            if (OnStreamStart is not null) await OnStreamStart();
            var update = new ChatResponseUpdate(ChatRole.Assistant, "stream answer") { ModelId = "native-model" };
            if (Usage is not null) update.Contents.Add(new UsageContent(Usage));
            yield return update;
            await Task.CompletedTask;
            if (StreamFailure is not null) throw StreamFailure;
        }
    }

    private sealed class RecordingRecrobyLogger : ILogger<RgfRecrobyService>
    {
        public List<(LogLevel Level, Exception? Exception, Dictionary<string, object?> State)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary()));
    }

    private sealed class RecordingGuard(List<AIProviderRequestContext> contexts) : IAIProviderRequestGuard
    {
        public ValueTask BeforeRequestAsync(AIProviderRequestContext context, CancellationToken cancellationToken = default)
        {
            lock (contexts) contexts.Add(context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingUsageHandler(List<AIProviderUsageContext> contexts) : IAIProviderUsageHandler
    {
        public ValueTask OnUsageAsync(AIProviderUsageContext context, CancellationToken cancellationToken = default)
        {
            lock (contexts) contexts.Add(context);
            return ValueTask.CompletedTask;
        }
    }
}

[CollectionDefinition("Recroby dictionary", DisableParallelization = true)]
public sealed class RecrobyDictionaryCollection;

// RecroDict has a static database-backed cache; seed it without introducing a second localization path.
internal sealed class RecrobyDictionaryFixture : IDisposable
{
    private readonly System.Reflection.FieldInfo field = typeof(Recrovit.RecroGridFramework.RecroDict)
        .GetField("_localization", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    private readonly object? original;
    private readonly System.Collections.IDictionary cache;
    private readonly Type recordType = typeof(Recrovit.RecroGridFramework.RecroDict)
        .GetNestedType("RecroDictRec", System.Reflection.BindingFlags.NonPublic)!;

    public RecrobyDictionaryFixture()
    {
        original = field.GetValue(null);
        cache = (System.Collections.IDictionary)Activator.CreateInstance(field.FieldType)!;
        field.SetValue(null, cache);
        Add("eng", "RGF.Language", "hun", "Hungarian");
        Add("eng", "RGF.Language", "eng", "English");
        Add("eng", "RGF.Recroby.Continuation.host.workflow.host.ask", "unused", "Unused");
    }

    public void Add(string language, string scope, string key, string value)
    {
        if (!cache.Contains(language))
        {
            var scopeType = typeof(SortedDictionary<,>).MakeGenericType(typeof(string), typeof(Dictionary<,>).MakeGenericType(typeof(string), recordType));
            cache[language] = Activator.CreateInstance(scopeType)!;
        }
        var scopes = (System.Collections.IDictionary)cache[language]!;
        if (!scopes.Contains(scope))
            scopes[scope] = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), recordType))!;
        var record = Activator.CreateInstance(recordType, nonPublic: true)!;
        recordType.GetProperty("Key")!.SetValue(record, key);
        recordType.GetProperty("Value")!.SetValue(record, value);
        ((System.Collections.IDictionary)scopes[scope]!)[key] = record;
    }

    public void FailTranslation(string language, string scope, string key, Exception failure, Action? beforeFailure = null)
    {
        var scopes = (System.Collections.IDictionary)cache[language]!;
        var existing = (System.Collections.IDictionary)scopes[scope]!;
        var comparer = new TranslationFailureComparer(key, failure, beforeFailure);
        var replacement = (System.Collections.IDictionary)Activator.CreateInstance(existing.GetType(), comparer)!;
        foreach (System.Collections.DictionaryEntry entry in existing) replacement.Add(entry.Key, entry.Value);
        scopes[scope] = replacement;
        comparer.Enabled = true;
    }

    // Inject failure at the actual RecroDict lookup without changing production localization dependencies.
    private sealed class TranslationFailureComparer(string key, Exception failure, Action? beforeFailure) : IEqualityComparer<string>
    {
        public bool Enabled { get; set; }
        public bool Equals(string? left, string? right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
        public int GetHashCode(string value)
        {
            if (Enabled && StringComparer.OrdinalIgnoreCase.Equals(value, key))
            {
                beforeFailure?.Invoke();
                throw failure;
            }
            return StringComparer.OrdinalIgnoreCase.GetHashCode(value);
        }
    }

    public void Dispose() => field.SetValue(null, original);
}
