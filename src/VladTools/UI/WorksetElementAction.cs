namespace VladTools.UI
{
    /// <summary>
    /// What happens to the elements standing in a workset that is being removed. Revit's own
    /// "Delete Workset" dialog asks exactly this and offers exactly these two answers — the API
    /// mirrors them one to one in <c>DeleteWorksetOption</c>, and there is no third way: a workset
    /// cannot be deleted while anything is still in it.
    /// </summary>
    internal enum WorksetElementAction
    {
        /// <summary>
        /// Move the elements into another workset and delete only the workset itself.
        /// The default, and deliberately so: it is the answer that loses nothing.
        /// </summary>
        Move,

        /// <summary>Delete the elements together with the workset — the model loses geometry.</summary>
        Delete
    }
}
