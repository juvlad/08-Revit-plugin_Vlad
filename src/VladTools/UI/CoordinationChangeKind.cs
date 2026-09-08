namespace VladTools.UI
{
    /// <summary>
    /// Что разошлось у элемента проекта с координационным файлом.
    ///
    /// Один элемент может дать две строки таблицы — отдельно про положение и отдельно про имя:
    /// в «Просмотре координации» Revit это тоже два разных изменения, и принимать их порознь
    /// пользователь должен уметь.
    /// </summary>
    internal enum CoordinationChangeKind
    {
        /// <summary>Ось сдвинута или повёрнута, у уровня другая отметка.</summary>
        Position,

        /// <summary>В координационном файле элемент назван иначе.</summary>
        Name,

        /// <summary>За связью следят, а подходящего элемента в ней не нашлось.</summary>
        Missing,

        /// <summary>В координационном файле элемент есть, а в проекте за ним никто не следит.</summary>
        New,

        /// <summary>Разница есть, но выразить её переносом и поворотом нельзя.</summary>
        Unsupported
    }

    /// <summary>Подписи и правила для <see cref="CoordinationChangeKind"/>.</summary>
    internal static class CoordinationChangeKinds
    {
        public static string Label(CoordinationChangeKind kind)
        {
            switch (kind)
            {
                case CoordinationChangeKind.Position:
                    return "Положение";
                case CoordinationChangeKind.Name:
                    return "Имя";
                case CoordinationChangeKind.Missing:
                    return "Нет в координационном файле";
                case CoordinationChangeKind.New:
                    return "Новый в координационном файле";
                default:
                    return "Применить нельзя";
            }
        }

        /// <summary>
        /// Изменение кнопка умеет применить. Остальные виды идут в таблицу только показать:
        /// удалять оси и уровни проекта кнопка не берётся (за уровнем уходит всё, что на нём
        /// стоит), а завести мониторинг на новый элемент связи Revit API не позволяет вовсе —
        /// это делается только руками, через «Копирование/Мониторинг».
        /// </summary>
        public static bool CanApply(CoordinationChangeKind kind)
        {
            return kind == CoordinationChangeKind.Position || kind == CoordinationChangeKind.Name;
        }
    }
}
