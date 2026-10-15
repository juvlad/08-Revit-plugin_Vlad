using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Keeps a ribbon button pressable with no document open at all. Revit greys out an add-in's
    /// buttons until a document is open unless the button names a class like this one — and a button
    /// that works on a batch of models it opens itself ("Sheets") has no need for one: making the user
    /// open some model first, only to have it in the way, is exactly what a batch is meant to spare.
    ///
    /// Revit asks this on every refresh of the ribbon, so it answers without looking at anything.
    /// It is public with a parameterless constructor because Revit creates it by name.
    /// </summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return true;
        }
    }
}
