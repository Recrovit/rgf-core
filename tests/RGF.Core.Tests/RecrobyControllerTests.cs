using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Recrovit.AI.Core;
using Recrovit.AI.Core.Workflows;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;
using Recrovit.RecroGridFramework.Core.AI.Recroby;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;
using Recrovit.RecroGridFramework.Core.AI.Recroby.Http;
using Recrovit.RecroGridFramework.Identity;
using Xunit;

namespace RGF.Core.Tests;

public sealed class RecrobyControllerTests
{
    [Theory]
    [InlineData(WorkflowStatus.Completed, true)]
    [InlineData(WorkflowStatus.Failed, false)]
    public async Task AuthenticatedIdentityAndOriginalRequestReachServiceAndResponseRemainsHttp200(
        WorkflowStatus status, bool success)
    {
        var fixture = new Fixture();
        fixture.Workflows.Status = status;
        // Neither an unknown top-level JSON member nor custom parameters can override server identity.
        var request = JsonSerializer.Deserialize<RgfAiRequest>("""
            { "userId": "attacker", "currentUserMessage": "Hello",
              "customParams": { "userId": "attacker" } }
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var result = await fixture.Controller.ExecuteAsync(request, Token);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        var response = Assert.IsType<RgfAiResponse>(ok.Value);
        Assert.Equal(success, response.Success);
        Assert.Equal("workflow answer", response.Message);
        Assert.Equal("conversation", response.ConversationId);
        Assert.Equal("run", response.WorkflowRunId);
        Assert.Equal(status.ToString(), response.WorkflowStatus);
        Assert.NotEmpty(response.ConversationToken!);
        Assert.Same(fixture.User, fixture.Identity.Principal);
        Assert.Equal("server-user", fixture.Extension.UserId);
        Assert.Same(request, fixture.Extension.Request);
        Assert.Equal("server-user", Assert.IsType<RgfRecrobyContext>(fixture.Workflows.Context).UserId);
        Assert.Equal("Hello", fixture.Workflows.Instruction);
        Assert.Equal(Token, fixture.Workflows.Token);
        Assert.Equal(1, fixture.Workflows.Calls);
        var protectedIdentity = JsonSerializer.Deserialize<JsonElement>(fixture.Protection
            .CreateProtector("RGF.Recroby.ConversationIdentity.v1").Unprotect(response.ConversationToken!));
        Assert.Equal("server-user", protectedIdentity.GetProperty("UserId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingIdentityReturns401WithoutExecutingService(string? userId)
    {
        var fixture = new Fixture();
        fixture.Identity.UserId = userId;
        var result = await fixture.Controller.ExecuteAsync(new() { CurrentUserMessage = "Hello" }, Token);
        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Null(fixture.Extension.Request);
        Assert.Equal(0, fixture.Workflows.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task EmptyInstructionReturns400WithoutExecutingService(string instruction)
    {
        var fixture = new Fixture();
        var result = await fixture.Controller.ExecuteAsync(new() { CurrentUserMessage = instruction }, Token);
        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Null(fixture.Extension.Request);
        Assert.Equal(0, fixture.Workflows.Calls);
    }

    [Fact]
    public async Task UnauthorizedConversationReturns403WithoutStartingWorkflow()
    {
        var fixture = new Fixture();
        var result = await fixture.Controller.ExecuteAsync(new()
        { CurrentUserMessage = "Hello", ConversationId = "spoofed-without-token" }, Token);
        Assert.IsType<ForbidResult>(result.Result);
        Assert.Equal(0, fixture.Workflows.Calls);
    }

    [Fact]
    public void HttpAdapterRequiresAuthorization()
        => Assert.NotNull(typeof(RgfRecrobyController).GetCustomAttribute<AuthorizeAttribute>());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim("sub", "principal-subject")], "test"));
        public IdentityProxy Identity { get; }
        public RecordingExtension Extension { get; } = new();
        public RecordingWorkflows Workflows { get; } = new();
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public RgfRecrobyController Controller { get; }

        public Fixture()
        {
            var identity = DispatchProxy.Create<IRgfIdentityService, IdentityProxy>();
            Identity = (IdentityProxy)identity;
            var service = new RgfRecrobyService(Workflows, Workflows, Protection, Extension,
                NullLogger<RgfRecrobyService>.Instance);
            Controller = new(service, identity)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = User } } };
        }
    }

    private sealed class RecordingExtension : IRgfRecrobyExtension
    {
        public string? UserId { get; private set; }
        public RgfAiRequest? Request { get; private set; }
        public ValueTask<RgfRecrobyExecution> PrepareAsync(RgfRecrobyContext context, RgfAiRequest request,
            CancellationToken cancellationToken)
        {
            UserId = context.UserId;
            Request = request;
            return ValueTask.FromResult(new RgfRecrobyExecution("test.workflow", context, []));
        }
    }

    private sealed class RecordingWorkflows : IWorkflowClient, IWorkflowRunInspector
    {
        public WorkflowStatus Status { get; set; } = WorkflowStatus.Completed;
        public int Calls { get; private set; }
        public string? Instruction { get; private set; }
        public IWorkflowHostContext? Context { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<WorkflowOutput> StartWorkflowAsync(string workflowId, string? conversationId = null,
            string? userInstruction = null, IReadOnlyList<AgentInputData>? inputData = null,
            CancellationToken cancellationToken = default, IWorkflowHostContext? hostContext = null,
            AIExecutionOptions? executionOptions = null)
        {
            Calls++;
            Instruction = userInstruction;
            Context = hostContext;
            Token = cancellationToken;
            return Task.FromResult(new WorkflowOutput
            { ConversationId = "conversation", RunId = "run", Status = Status, AgentResults = [], Usage = new() { Total = new() }, FinalUserMessage = "workflow answer" });
        }
        public Task<WorkflowOutput> ResumeWorkflowAsync(string workflowRunId, string requestId,
            ContinuationResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public WorkflowRunInfo GetRunInfo(string runId) => throw new NotSupportedException();
    }

    // DispatchProxy avoids implementing unrelated identity operations in this focused adapter fixture.
    private class IdentityProxy : DispatchProxy
    {
        public string? UserId { get; set; } = "server-user";
        public ClaimsPrincipal? Principal { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IRgfIdentityService.GetUserIdAsync)) throw new NotSupportedException();
            Principal = (ClaimsPrincipal)args![0]!;
            return Task.FromResult(UserId!);
        }
    }
}
