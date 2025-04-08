namespace Recrovit.RecroGridFramework.Core;

#pragma warning disable CS0618 // Type or member is obsolete

public partial class RgfCore<TEntity> : RecroGridDBC<TEntity> where TEntity : class
{
    public RgfCore(IRecroGridContext rgContext) : base(rgContext) { }
}