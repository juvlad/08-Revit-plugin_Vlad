namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of a dimension type for the "Auto Dimensions" window: the window knows nothing
    /// about the Revit API, so instead of a <c>DimensionType</c> it gets the name and a numeric id
    /// (long — it survives both net48 and net8.0, where <c>ElementId</c> is represented differently).
    /// </summary>
    internal sealed class DimensionTypeInfo
    {
        public DimensionTypeInfo(long id, string name)
        {
            Id = id;
            Name = name ?? string.Empty;
        }

        public long Id { get; }
        public string Name { get; }

        public override string ToString()
        {
            return Name;
        }
    }
}
