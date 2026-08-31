using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// Итог чтения рабочих наборов у выбранных связей: сами имена наборов и то,
    /// у скольких моделей их прочитать не вышло.
    ///
    /// Чтение идёт без открытия моделей, но по сети, и на десятке связей занимает секунды —
    /// поэтому оно висит на кнопке, а не срабатывает само, и отдаёт результат вот такой
    /// сводкой, как проверка семейств в окне «Удалить общие параметры».
    /// </summary>
    internal sealed class LinkWorksetScan
    {
        public LinkWorksetScan(IReadOnlyList<string> names, int scanned, IReadOnlyList<string> failures)
        {
            Names = names ?? new List<string>();
            Scanned = scanned;
            Failures = failures ?? new List<string>();
        }

        /// <summary>Все имена наборов, встретившиеся хоть в одной прочитанной модели.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>Сколько моделей удалось прочитать.</summary>
        public int Scanned { get; }

        /// <summary>Модели, которые прочитать не удалось, с причиной.</summary>
        public IReadOnlyList<string> Failures { get; }
    }
}
