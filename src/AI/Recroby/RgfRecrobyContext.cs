using Recrovit.AI.Core.Workflows;

namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.recroby.architecture
/// <summary>Trusted identity captured by the authenticated host, never by model input.</summary>
/// <param name="UserId">The user ID supplied by the authenticated caller; not the Credit hook identity source.</param>
/// <param name="ConversationId">The verified existing conversation ID, or null for a new conversation.</param>
public record RgfRecrobyContext(string UserId, string? ConversationId = null) : IWorkflowHostContext
{
    /// <summary>Trusted invocation data supplied by the server for new-run preparation only.
    /// Never reconstructed from the request or used to replace a pending run's context.</summary>
    public IWorkflowHostContext? InitialHostContext { get; init; }
}
