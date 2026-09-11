namespace VladTools.UI
{
    /// <summary>
    /// A snapshot of one parameter group Revit offers ("Text", "Identity Data", and the rest): the
    /// <c>ForgeTypeId.TypeId</c> string a set stores and compares by, and the label Revit shows for it —
    /// the same pairing <c>Definition.GetGroupTypeId()</c> / <c>LabelUtils.GetLabelForGroup</c> already
    /// uses everywhere else in the project, confirmed identical across every supported Revit year.
    /// </summary>
    internal sealed class ParameterGroupInfo
    {
        public ParameterGroupInfo(string typeId, string label)
        {
            TypeId = typeId;
            Label = label;
        }

        public string TypeId { get; }

        public string Label { get; }
    }
}
