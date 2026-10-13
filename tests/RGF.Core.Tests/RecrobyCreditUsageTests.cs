using System.Reflection;
using Recrovit.RecroGridFramework.AI;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.AI.Core.Workflows;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI.Server;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Identity;
using Recrovit.RecroGridFramework.Security;
using Xunit;

namespace RGF.Core.Tests;

// RecroSec owns process-wide backend state; run this collection without other collections.
[CollectionDefinition("Recroby Credit backend", DisableParallelization = true)]
public sealed class RecrobyCreditBackendCollection;

[Collection("Recroby Credit backend")]
public sealed class RecrobyCreditUsageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleModelsAggregateSettlementsAndJsonDisclosesTokensOnlyToBackendAdmins(bool admin)
    {
        await using var fixture = new Fixture(admin: admin);
        fixture.Credit.Charge = 0.125m;
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("one", "provider-A", "native-A", new(11, 12, 23, 0, 5));
            await fixture.Call("two", "provider-A", "native-A", new(0, null, null, 4, null));
            await fixture.Call("three", "provider-B", "native-B", new(7, 8, 15, 0, 0));
        };
        var response = await fixture.Execute();
        Assert.True(response.Success);
        Assert.Null(response.Usage);
        var usage = Assert.IsType<RgfAiCreditUsage>(response.CreditUsage);
        Assert.Equal(0.375m, usage.ChargedCredit);
        Assert.True(usage.ExecutionTime >= TimeSpan.Zero);
        Assert.Equal(2, usage.Models.Count);
        var first = Assert.Single(usage.Models, model => model.ProviderId == "provider-A");
        Assert.Equal("native-A", first.ModelId);
        Assert.Equal(0.25m, first.ChargedCredit);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var jsonUsage = json.RootElement.GetProperty("creditUsage");
        Assert.Equal(admin, jsonUsage.TryGetProperty("tokens", out _));
        Assert.All(jsonUsage.GetProperty("models").EnumerateArray(), model => Assert.Equal(admin, model.TryGetProperty("tokens", out _)));
        if (admin)
        {
            Assert.Equal(18, usage.Tokens!.InputTokens);
            Assert.Null(usage.Tokens.OutputTokens);
            Assert.Null(usage.Tokens.TotalTokens);
            Assert.Equal(4, usage.Tokens.CachedInputTokens);
            Assert.Null(usage.Tokens.ReasoningTokens);
            Assert.Equal(11, first.Tokens!.InputTokens);
            Assert.Null(first.Tokens.TotalTokens);
        }
        else
        {
            Assert.Null(usage.Tokens);
            Assert.All(usage.Models, model => Assert.Null(model.Tokens));
        }
    }

    [Fact]
    public async Task DuplicateRequestIdsAndExistingSettlementsDoNotIncreaseCreditOrTokens()
    {
        await using var fixture = new Fixture(admin: true);
        fixture.Credit.Settlement = usage => new(usage.RequestId, 3m, usage.RequestId != "existing");
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("duplicate", "p", "m", new(2));
            await fixture.Call("duplicate", "p", "m", new(20));
            await fixture.Call("existing", "p", "m", new(200));
        };
        var response = await fixture.Execute();
        Assert.Equal(3, fixture.Credit.UsageCalls);
        Assert.Equal(3m, response.CreditUsage!.ChargedCredit);
        Assert.Equal(2, response.CreditUsage.Tokens!.InputTokens);
        Assert.Equal(3m, Assert.Single(response.CreditUsage.Models).ChargedCredit);
    }

    [Fact]
    public async Task ParallelRequestsAndConcurrentCallsAreIsolatedAcrossDiScopes()
    {
        await using var fixture = new Fixture();
        fixture.Credit.Settlement = usage => new(usage.RequestId, usage.InputTokens!.Value, true);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        fixture.Workflow.Execute = async instruction =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(Token);
            var count = long.Parse(instruction);
            await Task.WhenAll(Enumerable.Range(0, 10).Select(index => fixture.Call($"{instruction}-{index}", "p", "m", new(count))));
        };
        var responses = await Task.WhenAll(fixture.Execute("1"), fixture.Execute("2"));
        Assert.Equal(10m, responses[0].CreditUsage!.ChargedCredit);
        Assert.Equal(20m, responses[1].CreditUsage!.ChargedCredit);
        Assert.Equal(20, fixture.Credit.UsageCalls);
    }

    [Fact]
    public async Task ConversationTurnsHaveIndependentUsageAndClosedCollectorsIgnoreLateNotifications()
    {
        await using var fixture = new Fixture();
        fixture.Credit.Charge = 2m;
        AIProviderRequestContext? late = null;
        fixture.Workflow.Execute = async _ =>
        {
            late = await fixture.Authorize();
            await fixture.Notify("first", late, "p", "m", new(1));
        };
        var first = await fixture.Execute();
        await fixture.Notify("late", late!, "p", "m", new(99));
        fixture.Workflow.Execute = _ => fixture.Call("second", "p", "m", new(2));
        var second = await fixture.Execute("next", first);
        Assert.Equal(2m, first.CreditUsage!.ChargedCredit);
        Assert.Equal(2m, second.CreditUsage!.ChargedCredit);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(3, fixture.Credit.UsageCalls);
        await fixture.Call("outside", "p", "m", new(3));
        Assert.Equal(4, fixture.Credit.UsageCalls);
        Assert.Equal(2m, second.CreditUsage.ChargedCredit);
    }

    [Theory]
    [InlineData("handled")]
    [InlineData("denied")]
    [InlineData("waiting")]
    [InlineData("failed-output")]
    public async Task ReturnedWorkflowOutcomesPreserveEarlierSettledUsage(string outcome)
    {
        await using var fixture = new Fixture();
        fixture.Credit.Charge = 4m;
        fixture.Workflow.Status = outcome switch
        {
            "waiting" => WorkflowStatus.WaitingForInput,
            "failed-output" => WorkflowStatus.Failed,
            _ => WorkflowStatus.Completed
        };
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("settled", "p", "m", new(1));
            if (outcome == "handled") throw new InvalidOperationException("private failure");
            if (outcome == "denied")
            {
                fixture.Credit.Decisions.Enqueue(AiCreditDenialReason.InsufficientCredit);
                await fixture.Authorize();
            }
        };
        var response = await fixture.Execute();
        Assert.Equal(outcome == "waiting", response.Success);
        Assert.Equal(4m, response.CreditUsage!.ChargedCredit);
        Assert.Equal(4m, Assert.Single(response.CreditUsage.Models).ChargedCredit);
        Assert.NotNull(response.CreditState);
        Assert.DoesNotContain("private", response.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failure")]
    [InlineData("settlement-cancellation")]
    [InlineData("configuration")]
    [InlineData("wrapped-configuration")]
    [InlineData("aggregate-configuration")]
    public async Task UnsettledUsageIsUnknownAndRetainsPreviouslyFinalizedModelCharges(string outcome)
    {
        await using var fixture = new Fixture();
        fixture.Credit.Charge = 2m;
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("settled", "p", "m", new(1));
            fixture.Credit.OnUsage = outcome switch
            {
                "failure" => _ => throw new InvalidOperationException("settlement failed"),
                "settlement-cancellation" => _ => throw new OperationCanceledException(),
                "configuration" => _ => throw new AiCreditConfigurationException("private provider/model detail"),
                "wrapped-configuration" => _ => throw new System.Reflection.TargetInvocationException(
                    new AiCreditConfigurationException("private provider/model detail")),
                "aggregate-configuration" => _ => throw new System.Reflection.TargetInvocationException(
                    new AggregateException(new InvalidOperationException("other failure"),
                        new AiCreditConfigurationException("private provider/model detail"))),
                _ => null
            };
            await fixture.Call("unknown", "p", "m", outcome == "missing" ? null : new(9));
        };
        if (outcome == "settlement-cancellation")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Execute());
        else
        {
            var response = await fixture.Execute();
            Assert.Null(response.CreditUsage!.ChargedCredit);
            Assert.Equal(2m, Assert.Single(response.CreditUsage.Models).ChargedCredit);
            if (outcome.Contains("configuration", StringComparison.Ordinal))
            {
                Assert.False(response.Success);
                Assert.Equal("AiCredit.InvalidConfiguration", response.ErrorCode);
                Assert.Equal("Failed", response.WorkflowStatus);
                Assert.Equal("Recroby could not complete the request due to a configuration error. Please contact your system administrator.", response.Message);
                Assert.DoesNotContain("private", response.Message);
            }
        }
        fixture.Credit.OnUsage = null;
        fixture.Workflow.Execute = _ => fixture.Call("after", "p", "m", new(1));
        Assert.Equal(2m, (await fixture.Execute()).CreditUsage!.ChargedCredit);
    }

    [Theory]
    [InlineData(AiCreditMode.Disabled)]
    [InlineData(AiCreditMode.Unlimited)]
    [InlineData(AiCreditMode.Periodic)]
    [InlineData(AiCreditMode.Prepaid)]
    public async Task StateUsesAuthenticatedIdentityAndPostSettlementSnapshotsForAllModes(AiCreditMode mode)
    {
        await using var fixture = new Fixture();
        fixture.Credit.Mode = mode;
        fixture.Credit.Charge = 2m;
        fixture.Credit.Prepaid = new(8m, new DateOnly(2027, 1, 1), false, DateTime.UtcNow);
        fixture.Credit.Periods = [new(AiCreditPeriodType.Daily, 10m, 2m, DateTime.UtcNow.AddDays(1), DateTime.UtcNow),
            new(AiCreditPeriodType.Monthly, 100m, 20m, DateTime.UtcNow.AddMonths(1), DateTime.UtcNow)];
        fixture.Credit.OnUsage = _ =>
        {
            fixture.Credit.Prepaid = fixture.Credit.Prepaid! with { Balance = 6m };
            fixture.Credit.Periods = fixture.Credit.Periods.Select(period => period with { UsedCredit = period.UsedCredit + 2m }).ToArray();
        };
        fixture.Credit.OnState = user =>
        {
            Assert.Equal(fixture.UserId, user);
            Assert.Equal(1, fixture.Credit.UsageCalls);
        };
        fixture.Workflow.Execute = _ => fixture.Call("settled", "p", "m", new(1));
        var response = await fixture.Execute();
        Assert.Equal(mode, response.CreditState!.Mode);
        Assert.Equal(mode == AiCreditMode.Prepaid ? fixture.Credit.Prepaid : null, response.CreditState.Prepaid);
        Assert.Equal(mode == AiCreditMode.Periodic ? fixture.Credit.Periods : [], response.CreditState.Periods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateFailuresDoNotReplaceSuccessOrPrimaryWorkflowErrors(bool failWorkflow)
    {
        await using var fixture = new Fixture();
        fixture.Credit.OnState = _ => throw new InvalidOperationException("state failure");
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("settled", "p", "m", new(1));
            if (failWorkflow) throw new InvalidOperationException("workflow failure");
        };
        var response = await fixture.Execute();
        Assert.Equal(!failWorkflow, response.Success);
        Assert.Null(response.CreditState);
        Assert.NotNull(response.CreditUsage);
        Assert.DoesNotContain("state failure", response.Message);
    }

    [Fact]
    public async Task ProviderlessResponseHasKnownZeroUsageAndAuthenticatedState()
    {
        await using var fixture = new Fixture(providerMissing: true);
        var response = await fixture.Execute();
        Assert.Equal(RgfAiErrorCodes.AiProviderNotConfigured, response.ErrorCode);
        Assert.Equal(0m, response.CreditUsage!.ChargedCredit);
        Assert.Empty(response.CreditUsage.Models);
        Assert.NotNull(response.CreditState);
        Assert.Equal(0, fixture.Workflow.Calls);
    }

    [Fact]
    public async Task FailedIdentityResolutionDeniesTokenDisclosureAndStateWithoutChangingWorkflowResult()
    {
        await using var fixture = new Fixture(admin: true);
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("settled", "p", "m", new(1));
            fixture.Identity.FailUserId = true;
        };
        var response = await fixture.Execute();
        Assert.True(response.Success);
        Assert.Null(response.CreditState);
        Assert.Null(response.CreditUsage!.Tokens);
        Assert.All(response.CreditUsage.Models, model => Assert.Null(model.Tokens));
    }

    [Fact]
    public async Task UsageAssociationSurvivesMissingAmbientContextInNotificationScope()
    {
        await using var fixture = new Fixture();
        fixture.Credit.Charge = 3m;
        fixture.Workflow.Execute = async _ =>
        {
            var context = await fixture.Authorize();
            Task notification;
            using (ExecutionContext.SuppressFlow())
                notification = Task.Run(() => fixture.Notify("independent-scope", context, null, null, new(0)), Token);
            await notification;
        };
        var response = await fixture.Execute();
        Assert.Equal(3m, response.CreditUsage!.ChargedCredit);
        var model = Assert.Single(response.CreditUsage.Models);
        Assert.Null(model.ProviderId);
        Assert.Null(model.ModelId);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("missing")]
    [InlineData("existing")]
    [InlineData("denied")]
    public async Task NoNewChargeDistinguishesKnownZeroFromUnavailableSettlement(string outcome)
    {
        await using var fixture = new Fixture();
        fixture.Credit.Settlement = usage => new(usage.RequestId, outcome == "existing" ? 99m : 0m, outcome != "existing");
        if (outcome == "denied") fixture.Credit.Decisions.Enqueue(AiCreditDenialReason.UserDisabled);
        fixture.Workflow.Execute = _ => fixture.Call("request", "p", "m", outcome == "missing" ? null : new(0));
        var response = await fixture.Execute();
        Assert.Equal(outcome == "missing" ? (decimal?)null : 0m, response.CreditUsage!.ChargedCredit);
        Assert.Equal(outcome == "zero" ? 1 : 0, response.CreditUsage.Models.Count);
    }

    [Fact]
    public async Task BackendPermissionLookupFailureDefaultsToNoTokenDisclosure()
    {
        await using var fixture = new Fixture(admin: true);
        fixture.Workflow.Execute = async _ =>
        {
            await fixture.Call("settled", "p", "m", new(1));
            // An invalid backend cache entry forces RecroSec's existing role lookup failure path.
            fixture.SetBackendAdminCache("invalid permission state");
        };
        var response = await fixture.Execute();
        Assert.True(response.Success);
        Assert.Null(response.CreditUsage!.Tokens);
        Assert.All(response.CreditUsage.Models, model => Assert.Null(model.Tokens));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestCancellationDuringResponseQueriesPropagatesAndReleasesCollector(bool identityQuery)
    {
        await using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Action cancel = () =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        if (identityQuery) fixture.Identity.OnUserId = cancel;
        else fixture.Credit.OnState = _ => cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ExecuteAsync("trusted-facade-user",
            new() { CurrentUserMessage = "cancel" }, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        fixture.Identity.OnUserId = null;
        fixture.Credit.OnState = null;
        fixture.Credit.Charge = 2m;
        fixture.Workflow.Execute = _ => fixture.Call("after", "p", "m", new(1));
        Assert.Equal(2m, (await fixture.Execute()).CreditUsage!.ChargedCredit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropagatedCancellationAndAuthorizationCloseCollectionWithoutChangingSettlement(bool authorization)
    {
        await using var fixture = new Fixture();
        AIProviderRequestContext? context = null;
        fixture.Credit.Charge = 2m;
        fixture.Workflow.Execute = async _ =>
        {
            context = await fixture.Authorize();
            await fixture.Notify("before-error", context, "p", "m", new(1));
            if (authorization) throw new UnauthorizedAccessException();
            throw new OperationCanceledException();
        };
        if (authorization) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Execute());
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Execute());
        await fixture.Notify("late", context!, "p", "m", new(99));
        fixture.Workflow.Execute = _ => fixture.Call("next", "p", "m", new(1));
        Assert.Equal(2m, (await fixture.Execute()).CreditUsage!.ChargedCredit);
        Assert.Equal(3, fixture.Credit.UsageCalls);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;
        private readonly bool previousSingleUserMode;
        public string UserId { get; } = Guid.NewGuid().ToString();
        public AiCreditTestState Credit { get; } = new();
        public Workflows Workflow { get; } = new();
        public TestIdentityProxy Identity { get; }
        public RgfRecrobyService Service { get; }

        public Fixture(bool admin = false, bool providerMissing = false)
        {
            var host = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            Recrovit.RecroGridFramework.RecroGridConfig.Startup(host.Configuration, host.Environment);
            previousSingleUserMode = RecroSec.SingleUserMode;
            RecroSec.SingleUserMode = false;
            // Seed the existing backend permission cache, without introducing a production test seam.
            SetBackendAdminCache(admin);
            var services = new ServiceCollection();
            services.AddLogging();
            Credit.Register(services);
            services.AddRgfRecroby();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<IWorkflowClient>(Workflow);
            services.AddSingleton<IWorkflowRunInspector>(Workflow);
            if (providerMissing) services.AddSingleton(new Recrovit.AI.Runtime.AiRouteCatalog(new()));
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            scope = provider.CreateAsyncScope();
            Identity = (TestIdentityProxy)provider.GetRequiredService<IRgfIdentityService>();
            Identity.UserId = UserId;
            AiCreditTestState.Authenticate(provider, UserId);
            Service = scope.ServiceProvider.GetRequiredService<RgfRecrobyService>();
        }

        public void SetBackendAdminCache(object permission)
        {
            var cache = typeof(RecroSec).GetProperty("_adminCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            cache.GetType().GetMethod("Set")!.Invoke(cache, [UserId, permission, 300]);
        }

        public Task<RgfAiResponse> Execute(string message = "hello", RgfAiResponse? previous = null)
            => Service.ExecuteAsync("trusted-facade-user", new()
            {
                CurrentUserMessage = message, ConversationId = previous?.ConversationId,
                ConversationToken = previous?.ConversationToken,
                CustomParams = new() { ["userId"] = "attacker", ["IsAdmin"] = "true" }
            }, Token);

        public async Task<AIProviderRequestContext> Authorize()
        {
            var request = new AIProviderRequestContext();
            await using var guardScope = provider.CreateAsyncScope();
            await Assert.Single(guardScope.ServiceProvider.GetServices<IAIProviderRequestGuard>().OfType<RgfAiCreditRequestGuard>())
                .BeforeRequestAsync(request, Token);
            return request;
        }

        public async Task Call(string id, string? providerId, string? modelId, AIProviderUsage? usage)
            => await Notify(id, await Authorize(), providerId, modelId, usage);

        public async Task Notify(string id, AIProviderRequestContext request, string? providerId, string? modelId, AIProviderUsage? usage)
        {
            await using var usageScope = provider.CreateAsyncScope();
            await Assert.Single(usageScope.ServiceProvider.GetServices<IAIProviderUsageHandler>().OfType<RgfAiCreditUsageHandler>())
                .OnUsageAsync(new(id, request, "unaltered-route", modelId, usage, AIProviderUsageStatus.Completed, providerId), Token);
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            RecroSec.SingleUserMode = previousSingleUserMode;
        }
    }

    private sealed class Workflows : IWorkflowClient, IWorkflowRunInspector
    {
        public Func<string, Task> Execute { get; set; } = _ => Task.CompletedTask;
        public WorkflowStatus Status { get; set; } = WorkflowStatus.Completed;
        public int Calls { get; private set; }
        public async Task<WorkflowOutput> StartWorkflowAsync(string workflowId, string? conversationId = null,
            string? userInstruction = null, IReadOnlyList<AgentInputData>? inputData = null,
            CancellationToken cancellationToken = default, IWorkflowHostContext? hostContext = null,
            AIExecutionOptions? executionOptions = null)
        {
            Calls++;
            await Execute(userInstruction!);
            return new WorkflowOutput
            {
                ConversationId = conversationId ?? Guid.NewGuid().ToString(), RunId = Guid.NewGuid().ToString(),
                Status = Status, AgentResults = [], Usage = new() { Total = new() }, FinalUserMessage = "answer"
            };
        }
        public Task<WorkflowOutput> ResumeWorkflowAsync(string workflowRunId, string requestId,
            ContinuationResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public WorkflowRunInfo GetRunInfo(string runId) => throw new NotSupportedException();
    }
}
