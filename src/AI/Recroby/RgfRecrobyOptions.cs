namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

// RGF-DOC: rgf.core.initialization
/// <summary>Configures compatibility selection for existing protected Recroby workflows.</summary>
public sealed class RgfRecrobyOptions
{
    /// <summary>Preserves compatibility for protected conversations using a previously configured workflow.
    /// New general conversations always use the rgf.recroby workflow.</summary>
    /// <remarks>Defaults to rgf.recroby and must be nonblank. This setting is not an enablement switch
    /// and does not select a custom workflow for a new generic conversation.</remarks>
    public string WorkflowId { get; set; } = "rgf.recroby";
}
