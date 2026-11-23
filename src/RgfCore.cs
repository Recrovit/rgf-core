namespace Recrovit.RecroGridFramework.Core;

#pragma warning disable CS0618 // Type or member is obsolete

// RGF-DOC: rgf.legacy.server-side-class
/// <summary>Preserves the Core entry point for existing RGF entity classes.</summary>
/// <typeparam name="TEntity">The reference-type entity managed by the RGF context.</typeparam>
[Obsolete("Use RgBase<TEntity> from the Recrovit.RecroGridFramework namespace instead.")]
public partial class RgfCore<TEntity> : RecroGridDBC<TEntity> where TEntity : class
{
    /// <summary>Initializes entity operations with the supplied RGF context.</summary>
    /// <param name="rgContext">The context passed to the base entity implementation.</param>
    public RgfCore(IRecroGridContext rgContext) : base(rgContext) { }
}
