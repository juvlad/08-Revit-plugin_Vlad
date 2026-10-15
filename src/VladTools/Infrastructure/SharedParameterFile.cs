using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Reading a shared parameter file's definitions — shared by "Parameter Sets" (what a set is built
    /// from) and "Sheets" (which parameter is a sheet's number).
    /// </summary>
    internal static class SharedParameterFile
    {
        /// <summary>
        /// Every definition of the file, flattened out of its groups. The file Revit is already
        /// configured with is swapped out for the duration and restored right after — the API gives no
        /// way to open an arbitrary path directly, only "the current one", and this is an
        /// application-wide Revit setting that is not a button's to keep changed.
        /// </summary>
        public static IReadOnlyList<SharedParameterInfo> Read(Application app, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The shared parameter file was not found.", path);

            var original = app.SharedParametersFilename;

            try
            {
                app.SharedParametersFilename = path;
                var file = app.OpenSharedParameterFile();

                if (file == null)
                    throw new InvalidOperationException("Revit could not open this file as a shared parameter file.");

                var result = new List<SharedParameterInfo>();

                foreach (DefinitionGroup group in file.Groups)
                {
                    foreach (Definition definition in group.Definitions)
                    {
                        var external = definition as ExternalDefinition;
                        if (external == null)
                            continue;

                        result.Add(new SharedParameterInfo(external.GUID, external.Name, group.Name, DataTypeLabel(external)));
                    }
                }

                return result;
            }
            finally
            {
                try { app.SharedParametersFilename = original; }
                catch (Exception) { }
            }
        }

        /// <summary>The shared parameter file Revit is pointed at; empty when none, or when Revit will not say.</summary>
        public static string CurrentPath(Application app)
        {
            try { return app.SharedParametersFilename ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>"Text", "Length", … — for information only; empty when Revit cannot name it.</summary>
        public static string DataTypeLabel(Definition definition)
        {
            try
            {
                var dataType = definition?.GetDataType();
                return dataType != null && SpecUtils.IsSpec(dataType) ? LabelUtils.GetLabelForSpec(dataType) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
