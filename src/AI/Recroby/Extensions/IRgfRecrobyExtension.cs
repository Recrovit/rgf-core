using Recrovit.AI.Core.Workflows;
using Recrovit.RecroGridFramework.Abstraction.Contracts.AI;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby.Extensions;

// RGF-DOC: rgf.core.recroby.architecture
/// <summary>Server-side application preparation, separate from conversation execution and wire contracts.</summary>
/// <remarks>Implementations receive trusted invocation selections through RgfRecrobyContext.InitialHostContext.
/// Resume retains the original host context and input in Recrovit.AI; preparation runs only for a new run.</remarks>
public interface IRgfRecrobyExtension
{
    /// <summary>Selects an extension using protected workflow identity or trusted server-side context.</summary>
    /// <remarks>When workflowId is non-null, select using the existing conversation's protected workflow identity.
    /// When workflowId is null, select a new conversation using only trusted server-side context.
    /// This method only selects an extension; authorization remains in ValidateAsync and PrepareAsync.</remarks>
    /// <param name="workflowId">The protected workflow ID, or null for a new conversation.</param>
    /// <param name="hostContext">Stored context for pending resume, otherwise trusted invocation context.</param>
    /// <returns>True when this extension owns the selection. Core requires exactly one match.</returns>
    bool CanHandle(string? workflowId, IWorkflowHostContext? hostContext);

    /// <summary>Provides an application validation hook after identity verification and extension selection.</summary>
    /// <param name="context">Turn identity and the original stored host context for pending continuation.</param>
    /// <param name="request">The current client request; its parameters are not trusted authorization data.</param>
    /// <param name="cancellationToken">Cancellation of the current turn.</param>
    /// <remarks>Core invokes this hook for every selected application turn, including resume.
    /// The default implementation performs no authorization. Implementations must enforce application access.
    /// Generic rgf.recroby turns do not invoke application extensions.</remarks>
    ValueTask ValidateAsync(RgfRecrobyValidationContext context, RgfAiRequest request, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>Prepares an application workflow, trusted context and typed inputs for a new run.</summary>
    /// <param name="context">Turn identity and optional trusted invocation context.</param>
    /// <param name="request">The current client instruction and request settings.</param>
    /// <param name="cancellationToken">Cancellation of preparation.</param>
    /// <returns>A nonblank workflow ID, nonnull host context and nonnull input collection.</returns>
    /// <remarks>Called after validation for new runs, including turns after completion; never for pending resume.
    /// An existing conversation must retain its protected workflow ID.</remarks>
    ValueTask<RgfRecrobyExecution> PrepareAsync(
        RgfRecrobyContext context, RgfAiRequest request, CancellationToken cancellationToken);
}

/// <summary>Authenticated turn identity and, for resume, the original server-side run context.</summary>
/// <param name="UserId">The identity supplied by the authenticated server-side caller.</param>
/// <param name="ConversationId">The verified conversation ID, or null for a new conversation.</param>
/// <param name="IsContinuation">True when the protected token identifies a pending input request.</param>
/// <param name="WorkflowHostContext">Original stored run context for pending continuation; otherwise null.</param>
public sealed record RgfRecrobyValidationContext(string UserId, string? ConversationId,
    bool IsContinuation, IWorkflowHostContext? WorkflowHostContext);

/// <summary>Server-side workflow selection. Recrovit.AI types stay out of RGF request/response contracts.</summary>
/// <param name="WorkflowId">The registered workflow selected by the application.</param>
/// <param name="HostContext">Trusted context retained by the runtime for this run.</param>
/// <param name="InputData">Typed agent input values for the new run.</param>
public sealed record RgfRecrobyExecution(string WorkflowId, IWorkflowHostContext HostContext,
    IReadOnlyList<AgentInputData> InputData);
