using System;
using System.Linq;
using Autodesk.Revit.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Мелкие помощники для сборки ленты: вкладка/панель создаются один раз,
    /// кнопка добавляется одним вызовом.
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
                // Вкладка уже создана другой надстройкой или предыдущим запуском — это нормально.
            }

            var existing = application.GetRibbonPanels(tabName).FirstOrDefault(p => p.Name == panelName);
            return existing ?? application.CreateRibbonPanel(tabName, panelName);
        }

        public static PushButton AddPushButton(
            RibbonPanel panel,
            string name,
            string text,
            Type commandType,
            string tooltip,
            string longDescription = null,
            string iconBaseName = null)
        {
            var data = new PushButtonData(name, text, AssemblyPath, commandType.FullName)
            {
                ToolTip = tooltip,
                LongDescription = longDescription
            };

            if (!string.IsNullOrEmpty(iconBaseName))
            {
                data.Image = Icons.Load(iconBaseName + "_16.png");
                data.LargeImage = Icons.Load(iconBaseName + "_32.png");
            }

            return (PushButton)panel.AddItem(data);
        }
    }
}
