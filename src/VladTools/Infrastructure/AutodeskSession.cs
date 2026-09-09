using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// The Autodesk token of the user who is already signed in inside Revit itself.
    ///
    /// Why this way. Reading the BIM360/ACC folder tree needs access to Autodesk Platform Services,
    /// and that is granted over OAuth: normally an add-in registers an application of its own, keeps
    /// a client id and secret, and walks the user through a sign-in window. None of that is needed:
    /// Revit itself holds a three-legged token for the signed-in user, and hands it over from
    /// <c>Autodesk.Revit.AdWebServicesBase.GetInstance().GetOAuth2AccessToken()</c>
    /// <c>SSONET.dll</c> — the library that sits next to Revit.exe.
    ///
    /// **This is not a documented API.** The class appears neither in RevitAPI.dll nor in the help,
    /// and the method names may change in any Revit version. So everything here goes through
    /// reflection, and any failure is not a breakage but <see cref="Token"/> == null: the window then
    /// offers entering the GUIDs by hand. Catching <see cref="Exception"/> wholesale is deliberate:
    /// reflection over someone else's assembly throws a dozen different types, and none of them may bring the button down.
    /// </summary>
    internal static class AutodeskSession
    {
        private const string AssemblyName = "SSONET";
        private const string TypeName = "Autodesk.Revit.AdWebServicesBase";

        /// <summary>The user is signed in to Autodesk in this Revit session.</summary>
        public static bool IsLoggedIn
        {
            get
            {
                var instance = Instance();
                return instance != null && Call<bool>(instance, "IsLoggedIn");
            }
        }

        /// <summary>The name of the signed-in user, for the caption in the window; an empty string if unknown.</summary>
        public static string UserName
        {
            get
            {
                var instance = Instance();
                return instance == null ? string.Empty : Call<string>(instance, "GetLoginUserName") ?? string.Empty;
            }
        }

        /// <summary>
        /// A valid access token, or null if signing in did not work out.
        /// An expired token is refreshed first — Revit can do that on its own.
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

        /// <summary>Why the cloud project list cannot be shown. An empty string when all is well.</summary>
        public static string Obstacle()
        {
            if (Instance() == null)
                return "Revit did not report the Autodesk sign-in state (SSONET.dll): browsing BIM360 " +
                       "is unavailable in this Revit version, link the models by GUID instead.";

            if (!IsLoggedIn)
                return "You are not signed in to your Autodesk account. Sign in inside Revit (the account " +
                       "icon in the top right corner) and open the window again.";

            return Token == null
                ? "Revit did not hand over the Autodesk access token. Try signing out and back in."
                : string.Empty;
        }

        // ───────────────────────────── reflection ─────────────────────────────

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
        /// We take the assembly from the ones already loaded: inside Revit SSONET.dll has been up since
        /// startup, and loading it again from outside is a sure way to get a second copy of the native resources.
        /// The file next to Revit.exe is only the fallback.
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
                // It did not refresh — there will simply be no token, and the window will offer GUID entry.
            }
        }
    }
}
