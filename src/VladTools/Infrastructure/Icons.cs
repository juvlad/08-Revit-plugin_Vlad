using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VladTools.Infrastructure
{
    /// <summary>
    /// Loads the PNG icons embedded in the assembly (the Resources folder).
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
