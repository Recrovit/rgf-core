using Recrovit.AI.Core.Workflows;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;

/// <summary>Server-side application preparation, separate from conversation execution and wire contracts.</summary>
/// <remarks>Implementations receive trusted invocation selections through RgfRecrobyContext.InitialHostContext.
/// Resume retains the original host context and input in Recrovit.AI; preparation runs only for a new run.</remarks>
public interface IRgfRecrobyExtension
{
    /// <summary>Selects an extension using protected workflow identity or trusted server-side context.</summary>
    bool CanHandle(string? workflowId, IWorkflowHostContext? hostContext)
        => workflowId != "rgf.recroby" && (workflowId is not null || hostContext is not null);

    /// <summary>Application authorization on every turn, including resume, after token verification.</summary>
    ValueTask ValidateAsync(RgfRecrobyValidationContext context, RgfAiRequest request, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    ValueTask<RgfRecrobyExecution> PrepareAsync(
        RgfRecrobyContext context, RgfAiRequest request, CancellationToken cancellationToken);
}

/// <summary>Authenticated turn identity and, for resume, the original server-side run context.</summary>
public sealed record RgfRecrobyValidationContext(string UserId, string? ConversationId,
    bool IsContinuation, IWorkflowHostContext? WorkflowHostContext);

/// <summary>Server-side workflow selection. Recrovit.AI types stay out of RGF request/response contracts.</summary>
public sealed record RgfRecrobyExecution(string WorkflowId, IWorkflowHostContext HostContext,
    IReadOnlyList<AgentInputData> InputData);
