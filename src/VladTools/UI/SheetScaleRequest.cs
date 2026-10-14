using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>What the "Sheet Scale" window hands back: the new scale, what to do with templates that set the scale, and the checked views.</summary>
    internal sealed class SheetScaleRequest
    {
        public SheetScaleRequest(int scale, ScaleTemplateMode templates, IReadOnlyList<ScaleViewInfo> views)
        {
            Scale = scale;
            Templates = templates;
            Views = views;
        }

        /// <summary>100 for 1:100.</summary>
        public int Scale { get; }

        public ScaleTemplateMode Templates { get; }

        /// <summary>The checked views whose scale is not the new one yet, each once — a legend on several sheets included.</summary>
        public IReadOnlyList<ScaleViewInfo> Views { get; }
    }
}
