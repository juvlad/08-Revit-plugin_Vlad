using System;
using System.Collections.Generic;

namespace VladTools.UI
{
    /// <summary>
    /// Итог проверки загруженных семейств на метки размеров: какие общие параметры
    /// держат геометрию и сколько семейств удалось при этом открыть.
    ///
    /// Возвращается командой в окно «Удалить общие параметры проекта». В проекте меток
    /// размеров не видно — их приходится искать внутри каждого семейства, поэтому окно
    /// получает не сырые элементы Revit, а готовый список GUID.
    /// </summary>
    internal sealed class FamilyDimensionScan
    {
        public FamilyDimensionScan(
            ISet<string> parameterGuids,
            int openedFamilies,
            int reusedFamilies,
            IReadOnlyList<string> failures)
        {
            ParameterGuids = parameterGuids ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            OpenedFamilies = openedFamilies;
            ReusedFamilies = reusedFamilies;
            Failures = failures ?? new List<string>();
        }

        /// <summary>GUID общих параметров, которыми помечен хотя бы один размер.</summary>
        public ISet<string> ParameterGuids { get; }

        /// <summary>Сколько семейств пришлось открыть заново.</summary>
        public int OpenedFamilies { get; }

        /// <summary>Сколько взято из сохранённой проверки, без открытия.</summary>
        public int ReusedFamilies { get; }

        /// <summary>Семейства, которые открыть не удалось, с причиной.</summary>
        public IReadOnlyList<string> Failures { get; }
    }
}
