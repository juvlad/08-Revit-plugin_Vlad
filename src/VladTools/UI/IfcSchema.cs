using System;

namespace VladTools.UI
{
    /// <summary>
    /// The IFC versions "Export IFC" offers — the ones Revit's own IFC export dialog lists as setups,
    /// with the old and the niche (IFC2x2, BCA, COBie) left out. The window knows nothing about the Revit
    /// API, so this is not <c>IFCVersion</c> itself; <c>ExportIfcCommand</c> maps one onto the other.
    ///
    /// The order of the values is the order of the items in the window.
    /// </summary>
    internal enum IfcSchema
    {
        /// <summary>IFC2x3 Coordination View 2.0 — what Revit exports by default, and what most viewers and checkers read.</summary>
        Ifc2x3CoordinationView2,

        /// <summary>IFC4 Reference View — IFC4 for coordination: geometry as meshes, light files.</summary>
        Ifc4ReferenceView,

        /// <summary>IFC4 Design Transfer View — IFC4 for handing a model over to be edited further.</summary>
        Ifc4DesignTransferView,

        /// <summary>IFC2x3 Coordination View — the older 2x3 view, for software that will not take 2.0.</summary>
        Ifc2x3CoordinationView,

        /// <summary>IFC2x3 Basic FM Handover View — for facility management.</summary>
        Ifc2x3BasicFmHandover
    }

    /// <summary>Text for the window, the settings file and the report.</summary>
    internal static class IfcSchemaText
    {
        public static readonly IfcSchema[] All =
        {
            IfcSchema.Ifc2x3CoordinationView2,
            IfcSchema.Ifc4ReferenceView,
            IfcSchema.Ifc4DesignTransferView,
            IfcSchema.Ifc2x3CoordinationView,
            IfcSchema.Ifc2x3BasicFmHandover
        };

        public static string Caption(IfcSchema schema)
        {
            switch (schema)
            {
                case IfcSchema.Ifc2x3CoordinationView2:
                    return "IFC2x3 Coordination View 2.0";
                case IfcSchema.Ifc4ReferenceView:
                    return "IFC4 Reference View";
                case IfcSchema.Ifc4DesignTransferView:
                    return "IFC4 Design Transfer View";
                case IfcSchema.Ifc2x3CoordinationView:
                    return "IFC2x3 Coordination View";
                case IfcSchema.Ifc2x3BasicFmHandover:
                    return "IFC2x3 Basic FM Handover View";
                default:
                    return schema.ToString();
            }
        }

        public static IfcSchema Parse(string text, IfcSchema fallback)
        {
            foreach (var schema in All)
            {
                if (string.Equals(schema.ToString(), (text ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    return schema;
            }

            return fallback;
        }
    }
}
