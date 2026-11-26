using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

internal static class MascotArtwork
{
    public static BitmapSource Load(string dataDirectory, out string? warning, out bool bundledArtwork)
    {
        warning = null;
        bundledArtwork = false;
        var path = Path.Combine(dataDirectory, "mascot.png");
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > 20 * 1024 * 1024)
                    throw new InvalidDataException("画像は20MB以下にしてください。");
                return ReadPng(stream);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or FormatException or InvalidOperationException)
            {
                warning = "立ち絵の読込に失敗しました。同梱の暫定素材を使用します。 " + error.Message;
            }
        }

        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/HagoromoChan.Desktop;component/Assets/Mascot.png"))
            ?? throw new InvalidOperationException("同梱立ち絵が見つかりません。");
        bundledArtwork = true;
        using var bundled = resource.Stream;
        return ReadPng(bundled);
    }

    private static BitmapSource ReadPng(Stream stream)
    {
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 16 or > 4096 || frame.PixelHeight is < 16 or > 4096)
            throw new InvalidDataException("立ち絵は16～4096ピクセルのPNGにしてください。");
        // Require a visible character and at least one fully transparent pixel.
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var transparent = false;
        var visible = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            transparent |= pixels[i] == 0;
            visible |= pixels[i] > 0;
        }
        if (!transparent || !visible)
            throw new InvalidDataException("背景透過の立ち絵PNGを指定してください。");
        // Normalize metadata to 96 DPI. The window still constrains the real
        // physical pixel size to the source dimensions on each monitor.
        var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
