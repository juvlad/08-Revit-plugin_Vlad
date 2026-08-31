namespace VladTools.UI
{
    /// <summary>
    /// Снимок типа размера для окна «Авторазмеры»: окно про Revit API не знает, поэтому
    /// вместо <c>DimensionType</c> ему передаётся имя и числовой Id (long — переживает и net48,
    /// и net8.0, где у <c>ElementId</c> разные внутренние представления).
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
