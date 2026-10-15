using System;
using System.Linq;
using Autodesk.Revit.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Small helpers for building the ribbon: the tab and panel are created once,
    /// a button is added with a single call.
    /// </summary>
    internal static class Ribbon
    {
        private static string AssemblyPath => typeof(Ribbon).Assembly.Location;

        public static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string tabName, string panelName)
        {
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // The tab was already created by another add-in or by a previous run — that is fine.
            }

            var existing = application.GetRibbonPanels(tabName).FirstOrDefault(p => p.Name == panelName);
            return existing ?? application.CreateRibbonPanel(tabName, panelName);
        }

        /// <param name="availability">
        /// An <c>IExternalCommandAvailability</c> class deciding when the button can be pressed; null —
        /// Revit's own rule, "a document is open". See <see cref="AlwaysAvailable"/>.
        /// </param>
        public static PushButton AddPushButton(
            RibbonPanel panel,
            string name,
            string text,
            Type commandType,
            string tooltip,
            string longDescription = null,
            string iconBaseName = null,
            Type availability = null)
        {
            var data = new PushButtonData(name, text, AssemblyPath, commandType.FullName)
            {
                ToolTip = tooltip,
                LongDescription = longDescription
            };

            if (availability != null)
                data.AvailabilityClassName = availability.FullName;

            if (!string.IsNullOrEmpty(iconBaseName))
            {
                data.Image = Icons.Load(iconBaseName + "_16.png");
                data.LargeImage = Icons.Load(iconBaseName + "_32.png");
            }

            return (PushButton)panel.AddItem(data);
        }
    }
}
