using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Recrovit.AI.Core;
using Recrovit.AI.Core.DependencyInjection;
using Recrovit.AI.Core.Workflows;
using Recrovit.AI.Runtime;
using Recrovit.AI.Runtime.Configuration;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Workflows;
using Xunit;

namespace RGF.Core.Tests;

#pragma warning disable MAAI001 // Verify the same routing API used by Recrovit.AI.

public sealed class RecrobyTests
{
    [Fact]
    public async Task GenericConversationBypassesApplicationExtensionAndIgnoresCustomWorkflowSelection()
    {
        await using var fixture = new Fixture(custom: true);
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

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static RgfAiRequest Reply(RgfAiResponse response, string text) => new()
    { ConversationId = response.ConversationId, ConversationToken = response.ConversationToken, CurrentUserMessage = text };

    private sealed record HostContext(string Owner, int Selection) : RgfRecrobyContext(Owner);

    private sealed class HostExtension(IStructuredContractRegistry contracts) : IRgfRecrobyExtension
    {
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

    private sealed class AskOnce : IApplicationDecisionHandler
    {
        public bool AiAfterResume { get; set; }
        public List<IWorkflowHostContext?> Contexts { get; } = [];
        public ValueTask<DefaultAgentOutput<DecisionResult>> ExecuteAsync(ApplicationDecisionContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Contexts.Add(context.HostContext);
            var data = Assert.Single(context.Input.InputData);
            Assert.Equal("domain", data.Role);
            Assert.Equal("domain input", data.Value.Value.GetString());
            return ValueTask.FromResult(context.Input.CurrentUserInput is null
                ? new DefaultAgentOutput<DecisionResult> { InputRequest = new AgentInputRequest { Message = "More detail?" } }
                : new DefaultAgentOutput<DecisionResult> { Output = new() { TargetAgentIds = AiAfterResume ? ["host.chat"] : [] }, UserMessage = context.Input.CurrentUserInput.Text });
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
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public Fixture(bool custom = false, bool aiAfterResume = false)
        {
            Decision.AiAfterResume = aiAfterResume;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRecrovitAICore(configuration =>
            {
                configuration.AddDefaultRecrobyWorkflow();
                configuration.Contracts.Register<string>("host.input");
                configuration.Decisions.Register("host.ask", Decision);
                configuration.Workflows.Register(new WorkflowDefinition
                {
                    Id = "host.workflow", Name = "Host workflow", EntryAgentId = "host.ask",
                    Agents = [new AgentDefinition { Id = "host.ask", Name = "Ask", Type = AgentType.Decision,
                        DecisionExecutionMode = DecisionExecutionMode.Application, AllowInputRequest = true },
                        new AgentDefinition { Id = "host.chat", Name = "Chat", Type = AgentType.Executor,
                            Instruction = "Respond", AllowInputRequest = false, OutputContractId = "rgf.recroby.response",
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
                    { DefaultModel = "default", Models = new() { ["default"] = "native-default", ["alternate"] = "native-alternate" } } }
            }));
            services.AddSingleton(Protection);
            if (custom) services.AddRgfRecroby<HostExtension>(); else services.AddRgfRecroby();
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            scope = provider.CreateAsyncScope();
            Service = scope.ServiceProvider.GetRequiredService<RgfRecrobyService>();
            if (custom) Extension = Assert.IsType<HostExtension>(scope.ServiceProvider.GetRequiredService<IRgfRecrobyExtension>());
        }
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); await provider.DisposeAsync(); }
    }

    private sealed class ChatClient(string answer) : IChatClient
    {
        public List<ChatMessage[]> Messages { get; } = [];
        public List<string> Invocations { get; } = [];
        public Queue<string> DecisionKeys { get; } = new();
        public void Dispose() { }
        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(messages.ToArray());
            var format = Assert.IsType<ChatResponseFormatJson>(options?.ResponseFormat);
            var output = format.Schema!.Value.GetProperty("properties").GetProperty("output");
            if (output.TryGetProperty("properties", out var properties) && properties.TryGetProperty("decisionKey", out var decisionKeySchema))
            {
                Assert.Contains("Classify the user's current request.", options!.Instructions);
                var key = DecisionKeys.Count == 0 ? "chat" : DecisionKeys.Dequeue();
                Assert.Contains($"\"{key}\"", decisionKeySchema.GetRawText());
                Invocations.Add("intent");
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    JsonSerializer.Serialize(new { userMessage = (string?)null, output = new { decisionKey = key } }))));
            }
            Assert.Contains("string", output.GetProperty("type").EnumerateArray().Select(type => type.GetString()));
            var application = options!.Instructions?.Contains("application-specific request handling is not implemented yet.", StringComparison.Ordinal) == true;
            Invocations.Add(application ? "application" : "chat");
            var response = application ? "Application-specific request handling is not implemented yet." : answer;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(new { userMessage = response, output = response }))));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
