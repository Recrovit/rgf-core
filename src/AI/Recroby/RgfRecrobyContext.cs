using Recrovit.AI.Core.Workflows;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

/// <summary>Trusted identity captured by the authenticated host, never by model input.</summary>
public record RgfRecrobyContext(string UserId, string? ConversationId = null) : IWorkflowHostContext
{
    /// <summary>Trusted invocation data supplied by the server for new-run preparation only.
    /// Never reconstructed from the request or used to replace a pending run's context.</summary>
    public IWorkflowHostContext? InitialHostContext { get; init; }
}
