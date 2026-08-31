namespace VladTools.UI
{
    /// <summary>
    /// Снимок параметра семейства для окна формул. Окно работает без документа Revit,
    /// ему нужно только имя и то, можно ли этому параметру задать формулу.
    /// </summary>
    internal sealed class FamilyParameterInfo
    {
        public FamilyParameterInfo(string name, bool canAssignFormula, bool hasFormula)
        {
            Name = name;
            CanAssignFormula = canAssignFormula;
            HasFormula = hasFormula;
        }

        public string Name { get; }

        /// <summary>Revit запрещает формулу, например, у параметров-«отчётов» и у части встроенных.</summary>
        public bool CanAssignFormula { get; }

        /// <summary>У параметра уже есть формула — новая её заменит.</summary>
        public bool HasFormula { get; }
    }
}
