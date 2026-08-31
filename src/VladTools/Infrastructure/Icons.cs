using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Загрузка PNG-иконок, вшитых в сборку (папка Resources).
    /// </summary>
    internal static class Icons
    {
        public static ImageSource Load(string fileName)
        {
            var assembly = typeof(Icons).Assembly;
            using (var stream = assembly.GetManifestResourceStream("VladTools.Resources." + fileName))
            {
                if (stream == null)
                    return null;

                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
        }
    }
}
