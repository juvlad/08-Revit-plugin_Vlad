namespace VladTools.UI
{
    /// <summary>
    /// What "Sheet Scale" does with a view whose view template sets the scale. Such a template decides
    /// the scale: <c>View.Scale</c> cannot be changed past it — and that, on views already placed, is
    /// usually why a scale "would not change". The answers touch different amounts of the project,
    /// which is why the window asks rather than picks.
    ///
    /// The order of the values is the order of the items in the window.
    /// </summary>
    internal enum ScaleTemplateMode
    {
        /// <summary>"View Scale" is unticked in the template; every view keeps its scale, the checked ones get the new one.</summary>
        Release,

        /// <summary>The template itself gets the new scale, and every view using it follows — checked or not.</summary>
        ChangeTemplate,

        /// <summary>Such views are left alone and cannot be checked.</summary>
        Leave
    }

    /// <summary>Text for the window and the report — the window knows nothing about the Revit API, so it lives here.</summary>
    internal static class ScaleTemplateModeText
    {
        public static readonly ScaleTemplateMode[] All =
        {
            ScaleTemplateMode.Release,
            ScaleTemplateMode.ChangeTemplate,
            ScaleTemplateMode.Leave
        };

        public static string Caption(ScaleTemplateMode mode)
        {
            switch (mode)
            {
                case ScaleTemplateMode.Release:
                    return "Stop the template setting the scale";
                case ScaleTemplateMode.ChangeTemplate:
                    return "Give the template the new scale";
                case ScaleTemplateMode.Leave:
                    return "Leave such views as they are";
                default:
                    return mode.ToString();
            }
        }

        public static string Description(ScaleTemplateMode mode)
        {
            switch (mode)
            {
                case ScaleTemplateMode.Release:
                    return "\"View Scale\" is unticked in the template's \"Include\" column. Every view using the template keeps the scale it " +
                           "has, the checked ones get the new one; everything else the template sets stays as it is.";
                case ScaleTemplateMode.ChangeTemplate:
                    return "The template itself gets the new scale, and every view using it follows — on sheets that are not checked, " +
                           "and on no sheet at all, too. Right when the template is meant for one scale only.";
                case ScaleTemplateMode.Leave:
                    return "Views whose template sets the scale cannot be checked. Change the template by hand " +
                           "(View → View Templates → Manage View Templates).";
                default:
                    return string.Empty;
            }
        }

        public static ScaleTemplateMode Parse(string text, ScaleTemplateMode fallback)
        {
            foreach (var mode in All)
            {
                if (string.Equals(mode.ToString(), (text ?? string.Empty).Trim(), System.StringComparison.OrdinalIgnoreCase))
                    return mode;
            }

            return fallback;
        }
    }
}
