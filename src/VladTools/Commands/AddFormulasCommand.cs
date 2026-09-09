using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VladTools.UI;

namespace VladTools.Commands
{
    /// <summary>
    /// Assigns formulas to the parameters of the open (host) family from the saved
    /// "parameter — formula" list. The list is shared by every family: put it together once and it
    /// is offered by itself from then on, and all the formulas are applied as a single batch.
    /// It does not touch nested families — only the open document itself.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AddFormulasCommand : IExternalCommand
    {
        private const string DialogTitle = "Add Formulas";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData?.Application?.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "There is no active document.";
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show(DialogTitle,
                    "The command works only in the family editor.\n" +
                    "Open a family (.rfa) and try again.");
                return Result.Cancelled;
            }

            try
            {
                var manager = doc.FamilyManager;

                var window = new AddFormulasWindow(Describe(manager));
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;

                if (window.ShowDialog() != true)
                    return Result.Cancelled;

                var applied = new List<string>();
                var failures = new List<string>();

                using (var transaction = new Transaction(doc, "Add formulas to parameters"))
                {
                    transaction.Start();

                    Apply(manager, window.Selected, applied, failures);

                    if (applied.Count == 0)
                        transaction.RollBack();
                    else
                        transaction.Commit();
                }

                Report(applied, failures);
                return Result.Succeeded;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// A snapshot of every family parameter for the window: it is what validates both the parameter
        /// named in a row and the names inside the formula. Not only shared parameters are taken — a formula may refer to any of them.
        /// </summary>
        private static List<FamilyParameterInfo> Describe(FamilyManager manager)
        {
            return manager.Parameters
                .Cast<FamilyParameter>()
                .Where(parameter => parameter?.Definition != null)
                .Select(parameter => new FamilyParameterInfo(
                    parameter.Definition.Name,
                    CanAssignFormula(parameter),
                    !string.IsNullOrEmpty(parameter.Formula)))
                .ToList();
        }

        private static bool CanAssignFormula(FamilyParameter parameter)
        {
            try
            {
                return parameter.CanAssignFormula;
            }
            catch (Exception)
            {
                return true; // We could not ask — so we do not forbid it; let Revit decide when writing.
            }
        }

        /// <summary>
        /// Writes the formulas one after another in a shared transaction. A failure on one row
        /// (a circular reference, say) does not cancel the rest — it goes into the final report.
        /// </summary>
        private static void Apply(
            FamilyManager manager,
            IReadOnlyList<FormulaRule> rules,
            List<string> applied,
            List<string> failures)
        {
            foreach (var rule in rules)
            {
                var name = rule.TrimmedName;
                var parameter = Find(manager, name);

                if (parameter == null)
                {
                    failures.Add(name + " — parameter not found");
                    continue;
                }

                try
                {
                    manager.SetFormula(parameter, rule.TrimmedFormula);
                    applied.Add(name);
                }
                catch (Exception exception)
                {
                    failures.Add(name + " — " + exception.Message);
                }
            }
        }

        private static FamilyParameter Find(FamilyManager manager, string name)
        {
            return manager.Parameters
                .Cast<FamilyParameter>()
                .FirstOrDefault(parameter =>
                    parameter?.Definition != null &&
                    string.Equals(parameter.Definition.Name, name, StringComparison.Ordinal));
        }

        private static void Report(IReadOnlyList<string> applied, IReadOnlyList<string> failures)
        {
            var text = applied.Count > 0
                ? "Formulas assigned: " + applied.Count + ".\n• " + string.Join("\n• ", applied)
                : "No formula was assigned.";

            if (failures.Count > 0)
            {
                const int limit = 15;
                text += "\n\nCould not be assigned (" + failures.Count + "):\n• " +
                        string.Join("\n• ", failures.Take(limit));

                if (failures.Count > limit)
                    text += "\n… and " + (failures.Count - limit) + " more";
            }

            TaskDialog.Show(DialogTitle, text);
        }
    }
}
