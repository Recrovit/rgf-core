namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.recroby.architecture
// Only issued and consumed inside the Core runtime; never a wire contract.
internal sealed record RgfRecrobyConversationIdentity(
    string UserId, string ConversationId, string RunId, string? RequestId, string WorkflowId, string? ModelOverride);
