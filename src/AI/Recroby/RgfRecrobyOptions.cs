namespace Recrovit.RecroGridFramework.Core.AI.Recroby;

public sealed class RgfRecrobyOptions
{
    /// <summary>Preserves compatibility for protected conversations using a previously configured workflow.
    /// New general conversations always use the rgf.recroby workflow.</summary>
    public string WorkflowId { get; set; } = "rgf.recroby";
}
