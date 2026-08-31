using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Токен Autodesk того пользователя, который уже вошёл в учётную запись в самом Revit.
    ///
    /// Зачем так. Чтобы прочитать дерево папок BIM360/ACC, нужен доступ к Autodesk Platform
    /// Services, а он выдаётся по OAuth: обычно надстройка регистрирует своё приложение,
    /// хранит client id/secret и гоняет пользователя через окно входа. Ничего этого не нужно:
    /// Revit сам держит трёхногий токен вошедшего пользователя, и его отдаёт
    /// <c>Autodesk.Revit.AdWebServicesBase.GetInstance().GetOAuth2AccessToken()</c>
    /// из <c>SSONET.dll</c> — библиотеки, которая лежит рядом с Revit.exe.
    ///
    /// **Это не документированный API.** Ни в RevitAPI.dll, ни в справке этого класса нет,
    /// имена методов могут поменяться в любой версии Revit. Поэтому всё здесь идёт рефлексией
    /// и любой отказ — не поломка, а <see cref="Token"/> == null: окно тогда предлагает
    /// ввести GUID руками. Ловится <see cref="Exception"/> целиком осознанно: рефлексия по чужой
    /// сборке бросает с десяток разных типов, и ни один из них не должен ронять кнопку.
    /// </summary>
    internal static class AutodeskSession
    {
        private const string AssemblyName = "SSONET";
        private const string TypeName = "Autodesk.Revit.AdWebServicesBase";

        /// <summary>Пользователь вошёл в Autodesk в этом сеансе Revit.</summary>
        public static bool IsLoggedIn
        {
            get
            {
                var instance = Instance();
                return instance != null && Call<bool>(instance, "IsLoggedIn");
            }
        }

        /// <summary>Имя вошедшего пользователя — для подписи в окне; неизвестно — пустая строка.</summary>
        public static string UserName
        {
            get
            {
                var instance = Instance();
                return instance == null ? string.Empty : Call<string>(instance, "GetLoginUserName") ?? string.Empty;
            }
        }

        /// <summary>
        /// Действующий токен доступа или null, если войти не получилось.
        /// Просроченный токен сначала пробуем обновить — Revit умеет это сам.
        /// </summary>
        public static string Token
        {
            get
            {
                var instance = Instance();
                if (instance == null || !Call<bool>(instance, "IsLoggedIn"))
                    return null;

                if (Call<bool>(instance, "IsOAuth2TokenExpired"))
                    Refresh(instance);

                var token = Call<string>(instance, "GetOAuth2AccessToken");
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
        }

        /// <summary>Почему список облачных проектов не показать. Всё в порядке — пустая строка.</summary>
        public static string Obstacle()
        {
            if (Instance() == null)
                return "Revit не отдал сведения о входе в Autodesk (SSONET.dll): в этой версии Revit " +
                       "просмотр BIM360 недоступен, свяжите модели по GUID.";

            if (!IsLoggedIn)
                return "Вы не вошли в учётную запись Autodesk. Войдите в Revit (значок учётной записи " +
                       "в правом верхнем углу) и откройте окно заново.";

            return Token == null
                ? "Revit не отдал токен доступа Autodesk. Попробуйте выйти и войти в учётную запись заново."
                : string.Empty;
        }

        // ───────────────────────────── рефлексия ─────────────────────────────

        private static object Instance()
        {
            try
            {
                var type = FindType();
                if (type == null)
                    return null;

                var getInstance = type.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static);
                return getInstance == null ? null : getInstance.Invoke(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Сборку берём из уже загруженных: внутри Revit SSONET.dll поднята с самого старта,
        /// а грузить её заново со стороны — верный способ получить вторую копию нативных ресурсов.
        /// Файл рядом с Revit.exe — только запасной путь.
        /// </summary>
        private static Type FindType()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));

            if (loaded != null)
                return loaded.GetType(TypeName);

            var folder = Path.GetDirectoryName(typeof(Autodesk.Revit.DB.Document).Assembly.Location);
            if (string.IsNullOrEmpty(folder))
                return null;

            var file = Path.Combine(folder, AssemblyName + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file).GetType(TypeName) : null;
        }

        private static T Call<T>(object instance, string method)
        {
            try
            {
                var info = instance.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (info == null)
                    return default(T);

                var value = info.Invoke(instance, null);
                return value is T ? (T)value : default(T);
            }
            catch (Exception)
            {
                return default(T);
            }
        }

        private static void Refresh(object instance)
        {
            try
            {
                var info = instance.GetType().GetMethod("RefreshOAuth2Token", BindingFlags.Public | BindingFlags.Instance);
                if (info != null)
                    info.Invoke(instance, new object[] { true });
            }
            catch (Exception)
            {
                // Не обновился — дальше просто не будет токена, и окно предложит ввод по GUID.
            }
        }
    }
}
