using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Comparing the project's grids and levels against the coordination file.
    ///
    /// **"Coordination Review" does not exist in the Revit API at all** — neither reading its list
    /// nor pressing "Accept" in it: checked by reflection against RevitAPI.dll 2022, 2024 and 2025,
    /// of the whole monitoring feature only <c>Element.IsMonitoringLinkElement</c>,
    /// <c>IsMonitoringLocalElement</c>, <c>GetMonitoredLinkElementIds</c> and
    /// <c>GetMonitoredLocalElementIds</c> are exposed. So the button computes the differences
    /// itself: it takes the grids and levels that monitor the link and compares them against the
    /// same-named ones inside the link itself.
    ///
    /// **The API does not hand over the "project element — link element" pair either.**
    /// <c>GetMonitoredLinkElementIds</c>, despite its name, does not return what the element
    /// monitors, but the link instances it is found in. So the pair is recovered by name: grid and
    /// level names are unique in a document, and monitoring keeps them in sync. What did not match
    /// by name is matched further by position: that is how a rename is found. An element that is
    /// both renamed and moved far away honestly ends up in "not in the file" plus "new in the
    /// file", rather than being matched at random.
    /// </summary>
    internal static class CoordinationCatalog
    {
        /// <summary>
        /// Below this difference the element is treated as unmoved. Zero will not do: Revit's
        /// internal unit is feet, and converting coordinates through a link transform does not
        /// reproduce an exact match even for an element that was never touched.
        /// </summary>
        private const double ToleranceMm = 0.1;

        /// <summary>A rotation smaller than this is rounding noise, not a designer's edit.</summary>
        private const double AngleToleranceDeg = 0.001;

        /// <summary>
        /// How far a positional match is searched for when the name did not match. A renamed
        /// element usually stays put, but it may have been moved at the same time. A pair is
        /// accepted only if there is exactly one candidate (see <see cref="MatchByPosition"/>):
        /// otherwise a deleted grid would pair up with a random new one, and the button would
        /// silently move the wrong thing.
        /// </summary>
        private const double RenameWindowMm = 300.0;

        /// <summary>The same angular tolerance for finding a pair: a rotated grid is still the same grid.</summary>
        private const double RenameAngleDeg = 1.0;

        /// <summary>
        /// A link's grid or level in project coordinates — what we compare against.
        /// The geometry is read once: from here on only numbers are in play.
        /// </summary>
        private sealed class GridSample
        {
            public string Name;
            public Curve Curve;
        }

        private sealed class LevelSample
        {
            public string Name;
            public double Elevation;
        }

        // ───────────────────────────── walking the project ─────────────────────────────

        /// <summary>
        /// Every project link monitored by at least one grid or level, together with the
        /// differences found. Links nothing monitors are left out of the list: they have nothing
        /// to do with coordination.
        /// </summary>
        /// <param name="failures">
        /// Where to put what could not be read. An empty list is the normal case: the reason for a
        /// failure has to reach the user, not turn opening the window into an exception.
        /// </param>
        public static IReadOnlyList<CoordinationScan> Scan(Document doc, List<string> failures)
        {
            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Element>();
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Element>();

            var byLink = new Dictionary<ElementId, List<Element>>();
            var unresolved = 0;

            foreach (var element in grids.Concat(levels))
            {
                if (!IsMonitoringLink(element))
                    continue;

                var links = MonitoredLinkIds(doc, element);
                if (links.Count == 0)
                {
                    // The element monitors a link, but Revit did not say which one. Guessing is not
                    // allowed: attributing it to the wrong link would move a grid by the wrong file.
                    unresolved++;
                    continue;
                }

                foreach (var link in links)
                {
                    List<Element> hosts;
                    if (!byLink.TryGetValue(link, out hosts))
                    {
                        hosts = new List<Element>();
                        byLink[link] = hosts;
                    }

                    hosts.Add(element);
                }
            }

            if (unresolved > 0)
            {
                failures.Add("Grids and levels whose link could not be identified: " + unresolved +
                             " — they will have to be checked in \"Coordination Review\" by hand.");
            }

            var scans = new List<CoordinationScan>();

            foreach (var pair in byLink)
            {
                var link = doc.GetElement(pair.Key) as RevitLinkInstance;
                if (link == null)
                    continue;

                scans.Add(Compare(doc, link, pair.Value, failures));
            }

            return scans
                .OrderByDescending(scan => scan.Rows.Count)
                .ThenBy(scan => scan.LinkName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>The element monitors something in a link. A failure means "does not monitor", not a breakage.</summary>
        private static bool IsMonitoringLink(Element element)
        {
            try
            {
                return element.IsMonitoringLinkElement();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The link instances an element monitors. The returned ids are run through the document:
        /// by the documentation these should be link instances, but that cannot be taken on faith —
        /// whatever does not turn out to be a <see cref="RevitLinkInstance"/> is not a link.
        /// </summary>
        private static IReadOnlyList<ElementId> MonitoredLinkIds(Document doc, Element element)
        {
            try
            {
                var ids = element.GetMonitoredLinkElementIds();
                if (ids == null)
                    return new List<ElementId>();

                return ids.Where(id => doc.GetElement(id) is RevitLinkInstance).ToList();
            }
            catch (Exception)
            {
                return new List<ElementId>();
            }
        }

        // ───────────────────────────── comparing against one link ─────────────────────────────

        private static CoordinationScan Compare(
            Document doc,
            RevitLinkInstance link,
            IReadOnlyList<Element> hosts,
            List<string> failures)
        {
            var scan = new CoordinationScan(link.Id, LinkName(doc, link), hosts.Count);

            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null)
            {
                // An unloaded link has nothing to compare against — that is not a failure but a
                // state, and the window honestly says so in the link's caption.
                return scan;
            }

            scan.IsLoaded = true;

            var rows = new List<CoordinationChangeRow>();
            var transform = link.GetTotalTransform();

            CompareGrids(hosts.OfType<Grid>().ToList(), linkDoc, transform, rows, failures);
            CompareLevels(hosts.OfType<Level>().ToList(), linkDoc, transform, rows, failures);

            // What the button will apply comes first, what has to be sorted out by hand comes
            // after. The order here is not cosmetic: a coordination file can have ten times more
            // new elements than moved ones, and mixed together they bury all the actual work.
            scan.Rows = rows
                .OrderBy(row => Weight(row.Kind))
                .ThenBy(row => row.IsLevel)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return scan;
        }

        /// <summary>The order of change kinds in the table: the actionable ones first, information after.</summary>
        private static int Weight(CoordinationChangeKind kind)
        {
            switch (kind)
            {
                case CoordinationChangeKind.Position:
                    return 0;
                case CoordinationChangeKind.Name:
                    return 1;
                case CoordinationChangeKind.Unsupported:
                    return 2;
                case CoordinationChangeKind.Missing:
                    return 3;
                default:
                    return 4;
            }
        }

        private static string LinkName(Document doc, RevitLinkInstance link)
        {
            try
            {
                var type = doc.GetElement(link.GetTypeId());
                return type != null ? type.Name : link.Name;
            }
            catch (Exception)
            {
                return link.Name;
            }
        }

        // ───────────────────────────── grids ─────────────────────────────

        private static void CompareGrids(
            IReadOnlyList<Grid> hosts,
            Document linkDoc,
            Transform transform,
            List<CoordinationChangeRow> rows,
            List<string> failures)
        {
            var samples = new List<GridSample>();

            foreach (var grid in new FilteredElementCollector(linkDoc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                var curve = SafeCurve(grid, failures);
                if (curve == null)
                    continue;

                samples.Add(new GridSample { Name = grid.Name, Curve = curve.CreateTransformed(transform) });
            }

            // The project grid curves are read once: the same grid takes part both in the
            // positional match and in the comparison itself, and a read failure must be voiced in
            // the report only once.
            var curves = new Dictionary<ElementId, Curve>();
            var readable = new List<Grid>();

            foreach (var host in hosts)
            {
                var curve = SafeCurve(host, failures);
                if (curve == null)
                    continue;

                curves[host.Id] = curve;
                readable.Add(host);
            }

            var pending = new List<Grid>();
            var free = MatchByName(readable, samples, sample => sample.Name,
                (host, sample) => EmitGrid(host, curves[host.Id], sample, rows), pending);

            // What did not match by name is not lost yet: this is what a rename looks like.
            MatchByPosition(pending, free,
                (host, sample) => Distance(curves[host.Id], sample),
                (host, sample) => EmitGrid(host, curves[host.Id], sample, rows));

            foreach (var host in pending)
            {
                var dependents = Dependents(host);

                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Missing, false, host.Name,
                    string.Empty,
                    Gone(false, dependents),
                    new DatumUpdate { HostId = host.Id },
                    dependents));
            }

            foreach (var sample in free)
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.New, false, sample.Name,
                    string.Empty,
                    "copy it in through \"Copy/Monitor\"",
                    null));
            }
        }

        /// <summary>
        /// How far a project grid differs from a link grid — used for finding a positional match.
        /// If it cannot be compared (an arc against a line, a changed radius) they are treated as
        /// different grids.
        /// </summary>
        private static double Distance(Curve host, GridSample sample)
        {
            double offsetMm;
            double angleDeg;
            DatumUpdate update;
            string problem;

            if (!TryGridDiff(host, sample.Curve, out offsetMm, out angleDeg, out update, out problem))
                return double.MaxValue;

            return Math.Abs(angleDeg) > RenameAngleDeg ? double.MaxValue : offsetMm;
        }

        private static void EmitGrid(
            Grid host,
            Curve curve,
            GridSample sample,
            List<CoordinationChangeRow> rows)
        {
            double offsetMm;
            double angleDeg;
            DatumUpdate update;
            string problem;

            if (!TryGridDiff(curve, sample.Curve, out offsetMm, out angleDeg, out update, out problem))
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Unsupported, false, host.Name,
                    string.Empty, problem + " — move the grid by hand", null));
            }
            else if (offsetMm > ToleranceMm || Math.Abs(angleDeg) > AngleToleranceDeg)
            {
                update.HostId = host.Id;

                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Position, false, host.Name,
                    Describe(offsetMm, angleDeg), string.Empty, update));
            }

            AddRename(host, sample.Name, false, rows);
        }

        /// <summary>
        /// The translation and rotation that align a project grid with a link grid.
        ///
        /// What is aligned is the **infinite line**, not the segment: a grid's length in the
        /// project is trimmed to fit its own views and has nothing to do with coordination, and
        /// matching the ends would mean spoiling somebody else's work. Hence the edit scheme: a
        /// rotation about the grid's midpoint (after which the directions match) plus a sideways shift.
        /// </summary>
        private static bool TryGridDiff(
            Curve host,
            Curve link,
            out double offsetMm,
            out double angleDeg,
            out DatumUpdate update,
            out string problem)
        {
            offsetMm = 0;
            angleDeg = 0;
            update = null;
            problem = null;

            var hostLine = host as Line;
            var linkLine = link as Line;

            if (hostLine != null && linkLine != null)
            {
                var hostDirection = Direction(hostLine);
                var linkDirection = Direction(linkLine);

                if (hostDirection == null || linkDirection == null)
                {
                    problem = "the grid does not lie in plan";
                    return false;
                }

                var angle = Math.Atan2(
                    hostDirection.CrossProduct(linkDirection).Z,
                    hostDirection.DotProduct(linkDirection));

                // A grid's direction has no sign: "left to right" and "right to left" are the same
                // grid, and a 180° flip must not be counted as a rotation.
                if (angle > Math.PI / 2)
                    angle -= Math.PI;
                if (angle <= -Math.PI / 2)
                    angle += Math.PI;

                var center = hostLine.Evaluate(0.5, true);
                var toLink = linkLine.GetEndPoint(0) - center;
                var along = toLink.DotProduct(linkDirection);
                var across = toLink - along * linkDirection;
                var translation = new XYZ(across.X, across.Y, 0);

                offsetMm = Mm(translation.GetLength());
                angleDeg = angle * 180.0 / Math.PI;

                update = new DatumUpdate
                {
                    Angle = Math.Abs(angleDeg) > AngleToleranceDeg ? angle : 0,
                    Center = center,
                    Translation = offsetMm > ToleranceMm ? translation : null
                };

                return true;
            }

            var hostArc = host as Arc;
            var linkArc = link as Arc;

            if (hostArc != null && linkArc != null)
            {
                if (Math.Abs(Mm(hostArc.Radius - linkArc.Radius)) > ToleranceMm)
                {
                    problem = "the arc grid's radius has changed";
                    return false;
                }

                // Rotating an arc about its own centre changes nothing but its ends — and, just
                // like with a straight grid, the ends have nothing to do with coordination.
                var shift = linkArc.Center - hostArc.Center;
                var translation = new XYZ(shift.X, shift.Y, 0);

                offsetMm = Mm(translation.GetLength());

                update = new DatumUpdate
                {
                    Center = hostArc.Center,
                    Translation = offsetMm > ToleranceMm ? translation : null
                };

                return true;
            }

            problem = "the grid in the link changed kind (a line instead of an arc, or the other way round)";
            return false;
        }

        // ───────────────────────────── levels ─────────────────────────────

        private static void CompareLevels(
            IReadOnlyList<Level> hosts,
            Document linkDoc,
            Transform transform,
            List<CoordinationChangeRow> rows,
            List<string> failures)
        {
            var samples = new List<LevelSample>();

            foreach (var level in new FilteredElementCollector(linkDoc).OfClass(typeof(Level)).Cast<Level>())
            {
                try
                {
                    // A link level's elevation is in the link's own coordinates. A point on its
                    // plane converts it to project coordinates under any transform, including a
                    // rotation and a vertical shift.
                    samples.Add(new LevelSample
                    {
                        Name = level.Name,
                        Elevation = transform.OfPoint(new XYZ(0, 0, level.Elevation)).Z
                    });
                }
                catch (Exception exception)
                {
                    failures.Add("Could not read a link level: " + LinkCatalog.Short(exception.Message));
                }
            }

            var pending = new List<Level>();
            var free = MatchByName(hosts, samples, sample => sample.Name,
                (host, sample) => EmitLevel(host, sample, rows), pending);

            MatchByPosition(pending, free,
                (host, sample) => Math.Abs(Mm(sample.Elevation - host.Elevation)),
                (host, sample) => EmitLevel(host, sample, rows));

            foreach (var host in pending)
            {
                var dependents = Dependents(host);

                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Missing, true, host.Name,
                    Mark(host.Elevation),
                    Gone(true, dependents),
                    new DatumUpdate { HostId = host.Id },
                    dependents));
            }

            foreach (var sample in free)
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.New, true, sample.Name,
                    Mark(sample.Elevation),
                    "copy it in through \"Copy/Monitor\"",
                    null));
            }
        }

        private static void EmitLevel(Level host, LevelSample sample, List<CoordinationChangeRow> rows)
        {
            var shiftMm = Mm(sample.Elevation - host.Elevation);

            if (Math.Abs(shiftMm) > ToleranceMm)
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Position, true, host.Name,
                    Mark(host.Elevation) + " → " + Mark(sample.Elevation) +
                    " (" + (shiftMm > 0 ? "+" : "") + Number(shiftMm) + " mm)",
                    string.Empty,
                    new DatumUpdate { HostId = host.Id, Elevation = sample.Elevation }));
            }

            AddRename(host, sample.Name, true, rows);
        }

        // ───────────────────────────── matching ─────────────────────────────

        /// <summary>
        /// Matches project elements to link elements by name. The ones with no match go into
        /// <paramref name="pending"/>, and what is left of the link's own elements is returned —
        /// <see cref="MatchByPosition"/> works with those two lists next.
        /// </summary>
        private static List<TSample> MatchByName<THost, TSample>(
            IReadOnlyList<THost> hosts,
            IReadOnlyList<TSample> samples,
            Func<TSample, string> name,
            Action<THost, TSample> emit,
            List<THost> pending)
            where THost : Element
        {
            var free = samples.ToList();

            // Revit keeps grid and level names unique, so the first one found is also the only
            // one; the dictionary is here in case a file still brings in a duplicate.
            var byName = new Dictionary<string, TSample>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var sample in samples)
            {
                if (!byName.ContainsKey(name(sample)))
                    byName[name(sample)] = sample;
            }

            foreach (var host in hosts)
            {
                TSample sample;
                if (!byName.TryGetValue(host.Name, out sample) || !free.Remove(sample))
                {
                    pending.Add(host);
                    continue;
                }

                emit(host, sample);
            }

            return free;
        }

        /// <summary>
        /// Matches what is left by position: an element standing in the same place is the same
        /// element, just renamed.
        ///
        /// A pair is taken **only if there is exactly one candidate within the window**. Otherwise
        /// a deleted grid would find itself a random new neighbour, and the button would silently
        /// move the wrong one — a mistake invisible on a plan.
        /// </summary>
        private static void MatchByPosition<THost, TSample>(
            List<THost> pending,
            List<TSample> free,
            Func<THost, TSample, double> distanceMm,
            Action<THost, TSample> emit)
        {
            foreach (var host in pending.ToList())
            {
                var near = free.Where(sample => distanceMm(host, sample) <= RenameWindowMm).ToList();
                if (near.Count != 1)
                    continue;

                emit(host, near[0]);

                pending.Remove(host);
                free.Remove(near[0]);
            }
        }

        private static void AddRename(Element host, string linkName, bool isLevel, List<CoordinationChangeRow> rows)
        {
            if (string.IsNullOrEmpty(linkName) || string.Equals(host.Name, linkName, StringComparison.CurrentCulture))
                return;

            rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Name, isLevel, host.Name,
                "\"" + host.Name + "\" → \"" + linkName + "\"", string.Empty,
                new DatumUpdate { HostId = host.Id, NewName = linkName }));
        }

        // ───────────────────────────── checking the result ─────────────────────────────

        /// <summary>
        /// Reads the link once more so the elements just edited can be checked against it.
        /// Returns null when there is nothing to check against — an unloaded or removed link.
        /// </summary>
        public static Verifier CreateVerifier(Document doc, ElementId linkId)
        {
            try
            {
                var link = doc.GetElement(linkId) as RevitLinkInstance;
                var linkDoc = link == null ? null : link.GetLinkDocument();
                if (linkDoc == null)
                    return null;

                return new Verifier(linkDoc, link.GetTotalTransform());
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Checks, after the transaction is committed, that an element really did end up where the
        /// coordination file has it.
        ///
        /// **This is not belt and braces.** <c>ElementTransformUtils.MoveElement</c> not throwing
        /// means Revit accepted the request, not that the element moved: a constraint, a group, a
        /// scope box or a failure resolved at commit time can all leave it where it was, and the
        /// command would have reported the shift it asked for as though it had happened. The same
        /// trap as with link worksets — <c>WorksetConfiguration</c> also only conveys a wish — and
        /// it is answered the same way: read the model back and say which of the three things
        /// happened.
        ///
        /// The twin is looked up by the element's **current** name, after the rename pass: that is
        /// what ties the two documents together everywhere in this file.
        /// </summary>
        internal sealed class Verifier
        {
            private readonly Transform _transform;
            private readonly Dictionary<string, Curve> _grids =
                new Dictionary<string, Curve>(StringComparer.CurrentCultureIgnoreCase);
            private readonly Dictionary<string, double> _levels =
                new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);

            internal Verifier(Document linkDoc, Transform transform)
            {
                _transform = transform;

                foreach (var grid in new FilteredElementCollector(linkDoc).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    try
                    {
                        if (!_grids.ContainsKey(grid.Name))
                            _grids[grid.Name] = grid.Curve.CreateTransformed(transform);
                    }
                    catch (Exception)
                    {
                        // A grid that cannot be read is one less thing to check against, not a
                        // reason to give up on checking the rest.
                    }
                }

                foreach (var level in new FilteredElementCollector(linkDoc).OfClass(typeof(Level)).Cast<Level>())
                {
                    try
                    {
                        if (!_levels.ContainsKey(level.Name))
                            _levels[level.Name] = transform.OfPoint(new XYZ(0, 0, level.Elevation)).Z;
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            /// <summary>
            /// Where the element stands now against the file. <paramref name="detail"/> is filled in
            /// only for <see cref="DatumVerdict.Off"/> — what is still left of the difference.
            /// </summary>
            public DatumVerdict Check(Element element, out string detail)
            {
                detail = string.Empty;

                try
                {
                    var level = element as Level;
                    if (level != null)
                    {
                        double elevation;
                        if (!_levels.TryGetValue(level.Name, out elevation))
                            return DatumVerdict.Unknown;

                        var shiftMm = Mm(elevation - level.Elevation);
                        if (Math.Abs(shiftMm) <= ToleranceMm)
                            return DatumVerdict.Aligned;

                        detail = "still off by " + Number(shiftMm) + " mm";
                        return DatumVerdict.Off;
                    }

                    var grid = element as Grid;
                    if (grid == null)
                        return DatumVerdict.Unknown;

                    Curve curve;
                    if (!_grids.TryGetValue(grid.Name, out curve))
                        return DatumVerdict.Unknown;

                    double offsetMm;
                    double angleDeg;
                    DatumUpdate update;
                    string problem;

                    if (!TryGridDiff(grid.Curve, curve, out offsetMm, out angleDeg, out update, out problem))
                        return DatumVerdict.Unknown;

                    if (offsetMm <= ToleranceMm && Math.Abs(angleDeg) <= AngleToleranceDeg)
                        return DatumVerdict.Aligned;

                    detail = "still off: " + Describe(offsetMm, angleDeg);
                    return DatumVerdict.Off;
                }
                catch (Exception)
                {
                    return DatumVerdict.Unknown;
                }
            }
        }

        // ───────────────────────────── gone from the file ─────────────────────────────

        /// <summary>
        /// How many other elements Revit would delete together with this grid or level.
        ///
        /// <c>GetDependentElements(null)</c> answers exactly the question the user is about to be
        /// asked — "what goes with it" — and it is read here, while scanning, rather than in the
        /// command: the number is the whole basis for the decision, and it has to be on the table
        /// **before** the deletion, not in the report after it. The element's own id is in the
        /// returned set and is not part of the price.
        ///
        /// A failure is not a breakage: the row still appears, just without a count — and the
        /// window's warning does not depend on the number, only the wording does.
        /// </summary>
        private static int Dependents(Element element)
        {
            try
            {
                var ids = element.GetDependentElements(null);
                if (ids == null)
                    return 0;

                return ids.Count(id => !Equals(id, element.Id));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// The note on an element that is gone from the coordination file. It says both why it was
        /// counted as gone — the pair is recovered by name and then by position, so "gone" means
        /// "neither matched" — and what deleting it would cost.
        /// </summary>
        private static string Gone(bool isLevel, int dependents)
        {
            var text = isLevel
                ? "no level with this name at this elevation in the file"
                : "no grid with this name in this position in the file";

            if (dependents > 0)
                text += "; deleting it takes " + dependents + " element(s) with it";

            return text;
        }

        // ───────────────────────────── odds and ends ─────────────────────────────

        /// <summary>A grid's curve; a failure means "nothing to compare against", not a broken command.</summary>
        private static Curve SafeCurve(Grid grid, List<string> failures)
        {
            try
            {
                return grid.Curve;
            }
            catch (Exception exception)
            {
                failures.Add("Could not read the grid \"" + grid.Name + "\": " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        /// <summary>
        /// A grid's direction in plan. A grid stands as a vertical plane, its curve is horizontal;
        /// if that is not the case there is no grid to speak of and nothing to compare.
        /// </summary>
        private static XYZ Direction(Line line)
        {
            var direction = new XYZ(line.Direction.X, line.Direction.Y, 0);
            return direction.GetLength() < 1e-9 ? null : direction.Normalize();
        }

        private static string Describe(double offsetMm, double angleDeg)
        {
            var parts = new List<string>();

            if (offsetMm > ToleranceMm)
                parts.Add("shift " + Number(offsetMm) + " mm");

            if (Math.Abs(angleDeg) > AngleToleranceDeg)
                parts.Add("rotation " + Number(angleDeg) + "°");

            return string.Join(", ", parts);
        }

        /// <summary>A level's elevation as it is written on a drawing: in millimetres, with a sign.</summary>
        private static string Mark(double feet)
        {
            var mm = Mm(feet);
            return (mm >= 0 ? "+" : "") + Number(mm);
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.CurrentCulture);
        }

        private static double Mm(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        }
    }
}
