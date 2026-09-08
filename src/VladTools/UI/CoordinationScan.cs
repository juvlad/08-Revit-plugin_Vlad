using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace VladTools.UI
{
    /// <summary>
    /// Итог сравнения проекта с одной связью: за ней следят такие-то оси и уровни,
    /// разошлось вот это.
    ///
    /// Связей, за которыми что-то следит, в проекте может оказаться несколько (базовый файл
    /// корпуса и общая посадка), поэтому окно перебирает их списком, а команда считает все
    /// сразу: чтение осей и уровней связи стоит недорого, а переключение между связями
    /// должно быть мгновенным.
    /// </summary>
    internal sealed class CoordinationScan
    {
        public CoordinationScan(ElementId linkId, string linkName, int monitoredCount)
        {
            LinkId = linkId;
            LinkName = linkName;
            MonitoredCount = monitoredCount;
            Rows = new List<CoordinationChangeRow>();
        }

        /// <summary>Экземпляр связи в проекте.</summary>
        public ElementId LinkId { get; }

        public string LinkName { get; }

        /// <summary>Сколько осей и уровней проекта следят за этой связью.</summary>
        public int MonitoredCount { get; }

        /// <summary>Связь загружена и её содержимое удалось прочитать.</summary>
        public bool IsLoaded { get; set; }

        public IReadOnlyList<CoordinationChangeRow> Rows { get; set; }

        /// <summary>Сколько изменений кнопка умеет применить.</summary>
        public int ApplicableCount => Rows.Count(row => row.CanApply);

        /// <summary>Подпись связи в выпадающем списке окна.</summary>
        public string Caption
        {
            get
            {
                if (!IsLoaded)
                    return LinkName + " — связь не загружена";

                if (Rows.Count == 0)
                    return LinkName + " — расхождений нет (следят: " + MonitoredCount + ")";

                return LinkName + " — расхождений: " + Rows.Count + " (следят: " + MonitoredCount + ")";
            }
        }
    }
}
