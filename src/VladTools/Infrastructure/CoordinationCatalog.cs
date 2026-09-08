using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VladTools.UI;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Сравнение осей и уровней проекта с координационным файлом.
    ///
    /// **«Просмотра координации» в Revit API нет вовсе** — ни прочитать его список, ни нажать
    /// в нём «Принять»: проверено рефлексией по RevitAPI.dll 2022, 2024 и 2025, из всего
    /// мониторинга наружу выведены только <c>Element.IsMonitoringLinkElement</c>,
    /// <c>IsMonitoringLocalElement</c>, <c>GetMonitoredLinkElementIds</c> и
    /// <c>GetMonitoredLocalElementIds</c>. Поэтому расхождения кнопка считает сама: берёт оси
    /// и уровни, которые следят за связью, и сравнивает их с одноимёнными внутри самой связи.
    ///
    /// **Пару «элемент проекта — элемент связи» API тоже не отдаёт.**
    /// <c>GetMonitoredLinkElementIds</c>, вопреки имени, возвращает не то, за чем элемент следит,
    /// а экземпляры связи, в которых это находится. Поэтому пара восстанавливается по имени:
    /// у осей и уровней имена в документе уникальны, и мониторинг их синхронизирует. Что не
    /// сошлось по имени — досопоставляется по совпадению положения: так находится переименование.
    /// Переименованный и одновременно далеко уехавший элемент честно попадает в «нет в файле» +
    /// «новый в файле», а не сопоставляется наугад.
    /// </summary>
    internal static class CoordinationCatalog
    {
        /// <summary>
        /// Ниже этого расхождения считаем, что элемент не двигался. Ноль сюда не годится:
        /// внутренние единицы Revit — футы, и пересчёт координат через трансформацию связи
        /// не воспроизводит точного совпадения даже у нетронутого элемента.
        /// </summary>
        private const double ToleranceMm = 0.1;

        /// <summary>Поворот меньше этого — тот же шум округления, а не правка проектировщика.</summary>
        private const double AngleToleranceDeg = 0.001;

        /// <summary>
        /// Насколько далеко ищется пара по положению, когда по имени не нашлось.
        /// Переименованный элемент обычно остаётся на месте, но его могли заодно и подвинуть.
        /// Пара принимается, только если кандидат ровно один (см. <see cref="MatchByPosition"/>):
        /// иначе удалённая ось спарилась бы со случайной новой, и кнопка молча уехала бы не туда.
        /// </summary>
        private const double RenameWindowMm = 300.0;

        /// <summary>Тот же допуск по углу для поиска пары: повёрнутая ось — всё ещё та же ось.</summary>
        private const double RenameAngleDeg = 1.0;

        /// <summary>
        /// Оси и уровни связи в координатах проекта — то, с чем сравниваем.
        /// Геометрия читается один раз: дальше в ходу только числа.
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

        // ───────────────────────────── обход проекта ─────────────────────────────

        /// <summary>
        /// Все связи проекта, за которыми следит хоть одна ось или уровень, вместе с найденными
        /// расхождениями. Связи, за которыми не следит ничего, в список не попадают: к координации
        /// они отношения не имеют.
        /// </summary>
        /// <param name="failures">
        /// Куда сложить то, что прочитать не удалось. Пустой список — обычное дело: причина
        /// отказа должна дойти до пользователя, а не превратить открытие окна в исключение.
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
                    // Элемент за связью следит, а за какой — Revit не сказал. Догадываться нельзя:
                    // приписав его к чужой связи, кнопка подвинула бы ось по чужому файлу.
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
                failures.Add("Осей и уровней, у которых не удалось определить связь: " + unresolved +
                             " — их придётся проверить в «Просмотре координации» вручную.");
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

        /// <summary>Элемент следит за чем-то в связи. Отказ означает «не следит», а не поломку.</summary>
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
        /// Экземпляры связи, за которыми следит элемент. Возвращённые id прогоняются через
        /// документ: по документации это должны быть экземпляры связи, но полагаться на слово
        /// нельзя — что не оказалось <see cref="RevitLinkInstance"/>, то и не связь.
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

        // ───────────────────────────── сравнение с одной связью ─────────────────────────────

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
                // Выгруженную связь сравнивать не с чем — это не отказ, а состояние,
                // и окно про него честно пишет в подписи связи.
                return scan;
            }

            scan.IsLoaded = true;

            var rows = new List<CoordinationChangeRow>();
            var transform = link.GetTotalTransform();

            CompareGrids(hosts.OfType<Grid>().ToList(), linkDoc, transform, rows, failures);
            CompareLevels(hosts.OfType<Level>().ToList(), linkDoc, transform, rows, failures);

            // Сперва то, что кнопка применит, потом то, что придётся разбирать руками.
            // Порядок здесь не косметика: новых элементов в координационном файле бывает
            // вдесятеро больше, чем уехавших, и вперемешку они прячут собой всю работу.
            scan.Rows = rows
                .OrderBy(row => Weight(row.Kind))
                .ThenBy(row => row.IsLevel)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return scan;
        }

        /// <summary>Порядок видов изменений в таблице: сначала работа, потом сведения.</summary>
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

        // ───────────────────────────── оси ─────────────────────────────

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

            // Кривые осей проекта читаются один раз: одна и та же ось попадает и в подбор пары
            // по положению, и в саму сверку, а отказ чтения должен прозвучать в отчёте однажды.
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

            // Что не сошлось по имени — ещё не потеряно: так выглядит переименование.
            MatchByPosition(pending, free,
                (host, sample) => Distance(curves[host.Id], sample),
                (host, sample) => EmitGrid(host, curves[host.Id], sample, rows));

            foreach (var host in pending)
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Missing, false, host.Name,
                    string.Empty,
                    "в координационном файле нет оси с таким именем и на этом месте",
                    null));
            }

            foreach (var sample in free)
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.New, false, sample.Name,
                    string.Empty,
                    "скопируйте её через «Копирование/Мониторинг»",
                    null));
            }
        }

        /// <summary>
        /// Насколько ось проекта разошлась с осью связи — для поиска пары по положению.
        /// Не сравнить (дуга против прямой, изменился радиус) — считаем, что это разные оси.
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
                    string.Empty, problem + " — перенесите ось вручную", null));
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
        /// Перенос и поворот, которыми ось проекта совмещается с осью связи.
        ///
        /// Совмещается именно **бесконечная прямая**, а не отрезок: длину оси в проекте
        /// подрезают под свои виды, к координации она отношения не имеет, и подгонять концы
        /// было бы порчей чужой работы. Отсюда и схема правки: поворот вокруг середины оси
        /// (после него направления совпадают) плюс сдвиг поперёк себя.
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
                    problem = "ось стоит не в плане";
                    return false;
                }

                var angle = Math.Atan2(
                    hostDirection.CrossProduct(linkDirection).Z,
                    hostDirection.DotProduct(linkDirection));

                // У направления оси нет знака: «слева направо» и «справа налево» — одна и та же
                // ось, и разворот на 180° поворотом считать нельзя.
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
                    problem = "у дуговой оси изменился радиус";
                    return false;
                }

                // Поворот дуги вокруг своего центра ничего не меняет, кроме её концов, —
                // а концы, как и у прямой оси, к координации отношения не имеют.
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

            problem = "ось в связи стала другого вида (прямая вместо дуги или наоборот)";
            return false;
        }

        // ───────────────────────────── уровни ─────────────────────────────

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
                    // Отметка уровня связи — в координатах связи. Через точку на его плоскости
                    // она переводится в координаты проекта при любой трансформации, включая
                    // поворот и смещение по вертикали.
                    samples.Add(new LevelSample
                    {
                        Name = level.Name,
                        Elevation = transform.OfPoint(new XYZ(0, 0, level.Elevation)).Z
                    });
                }
                catch (Exception exception)
                {
                    failures.Add("Уровень связи прочитать не удалось: " + LinkCatalog.Short(exception.Message));
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
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.Missing, true, host.Name,
                    string.Empty,
                    "в координационном файле нет уровня с таким именем и на этой отметке",
                    null));
            }

            foreach (var sample in free)
            {
                rows.Add(new CoordinationChangeRow(CoordinationChangeKind.New, true, sample.Name,
                    Mark(sample.Elevation),
                    "скопируйте его через «Копирование/Мониторинг»",
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
                    " (" + (shiftMm > 0 ? "+" : "") + Number(shiftMm) + " мм)",
                    string.Empty,
                    new DatumUpdate { HostId = host.Id, Elevation = sample.Elevation }));
            }

            AddRename(host, sample.Name, true, rows);
        }

        // ───────────────────────────── сопоставление ─────────────────────────────

        /// <summary>
        /// Сводит элементы проекта с элементами связи по имени. Не нашедшие пары уходят
        /// в <paramref name="pending"/>, а возвращается то, что осталось от связи, — с этими
        /// двумя списками дальше работает <see cref="MatchByPosition"/>.
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

            // Имена осей и уровней Revit держит уникальными, так что первый найденный —
            // он же единственный; словарь на случай, если файл всё-таки принесёт дубль.
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
        /// Досопоставляет оставшихся по положению: элемент, стоящий там же, — тот же самый,
        /// просто переименованный.
        ///
        /// Пара берётся, **только если кандидат в окне ровно один**. Иначе удалённая ось нашла бы
        /// себе случайную новую по соседству, и кнопка молча подвинула бы не то — осечка,
        /// которую на плане не видно.
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
                "«" + host.Name + "» → «" + linkName + "»", string.Empty,
                new DatumUpdate { HostId = host.Id, NewName = linkName }));
        }

        // ───────────────────────────── мелочи ─────────────────────────────

        /// <summary>Кривая оси; отказ означает «сравнить не с чем», а не поломку команды.</summary>
        private static Curve SafeCurve(Grid grid, List<string> failures)
        {
            try
            {
                return grid.Curve;
            }
            catch (Exception exception)
            {
                failures.Add("Ось «" + grid.Name + "» прочитать не удалось: " + LinkCatalog.Short(exception.Message));
                return null;
            }
        }

        /// <summary>
        /// Направление оси в плане. Ось стоит вертикальной плоскостью, её кривая горизонтальна;
        /// если это не так, оси у нас нет и сравнивать нечего.
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
                parts.Add("сдвиг " + Number(offsetMm) + " мм");

            if (Math.Abs(angleDeg) > AngleToleranceDeg)
                parts.Add("поворот " + Number(angleDeg) + "°");

            return string.Join(", ", parts);
        }

        /// <summary>Отметка уровня так, как её пишут на чертеже: в миллиметрах и со знаком.</summary>
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
