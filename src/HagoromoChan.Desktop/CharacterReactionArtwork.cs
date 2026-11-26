using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

internal sealed class CharacterReactionArtwork
{
    private readonly BitmapSource _original;
    private readonly BundledReactionDrawing? _bundled;
    private readonly CharacterReactionPack? _pack;
    private readonly Dictionary<BitmapSource, DrawingImage> _packImages = new();
    private double _displayScale = 1;

    public CharacterReactionArtwork(BitmapSource original, string dataDirectory, bool bundledArtwork)
    {
        _original = original;
        var packDirectory = Path.Combine(dataDirectory, "reactions", "default");
        try
        {
            if (File.Exists(Path.Combine(packDirectory, "pack.json")))
                _pack = CharacterReactionPack.Load(packDirectory, bundledArtwork,
                    Path.Combine(dataDirectory, "mascot.png"));
            else if (bundledArtwork)
                _bundled = new BundledReactionDrawing(original);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
            or FormatException or NotSupportedException or ArgumentException or InvalidOperationException
            or OverflowException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        {
            Warning = "アクション素材を読み込めないため、静止画で表示します。" + error.Message;
        }
    }

    public bool CanAnimate => _bundled is not null || _pack is not null;
    public ImageSource RestingImage => _pack is null
        ? _bundled?.RestingImage ?? _original
        : Frame("frozen", 0);
    public string? Warning { get; }

    public void SetDisplayScale(double physicalPixelsPerArtworkPixel)
    {
        if (!double.IsFinite(physicalPixelsPerArtworkPixel) || physicalPixelsPerArtworkPixel <= 0)
            return;
        var scale = Math.Min(1, physicalPixelsPerArtworkPixel);
        if (_displayScale == scale) return;
        _displayScale = scale;
        _bundled?.SetDisplayScale(scale);
        _packImages.Clear();
    }

    // progress covers a complete action (or one cycle of an idle/wait loop).
    // Gaze uses screen coordinates, so (0, -1) looks straight up.
    public ImageSource Frame(string action, double progress, double gazeX = 0, double gazeY = -1)
    {
        progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        gazeX = double.IsFinite(gazeX) ? Math.Clamp(gazeX, -1, 1) : 0;
        gazeY = double.IsFinite(gazeY) ? Math.Clamp(gazeY, -1, 1) : -1;
        if (_pack is null) return _bundled?.Frame(action, progress, gazeX, gazeY, Environment.TickCount64) ?? _original;

        var bitmap = _pack.Select(action, progress, gazeX, gazeY);
        if (_packImages.TryGetValue(bitmap, out var result)) return result;
        double width = _original.PixelWidth, height = _original.PixelHeight;
        var size = Math.Min(1 / _displayScale, Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight));
        var scaledWidth = bitmap.PixelWidth * size;
        var scaledHeight = bitmap.PixelHeight * size;
        var left = Math.Clamp(width / 2 - size * _pack.AnchorX, 0, Math.Max(0, width - scaledWidth));
        // Align the shared character anchor across cells and keep all transparent margins.
        var top = Math.Clamp(height * (_pack.AnchorY / bitmap.PixelHeight) - size * _pack.AnchorY,
            0, Math.Max(0, height - scaledHeight));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            context.DrawImage(bitmap, new Rect(left, top, scaledWidth, scaledHeight));
        }
        result = new DrawingImage(drawing);
        result.Freeze();
        _packImages.Add(bitmap, result);
        return result;
    }

    internal static int LookDirection(double x, double y)
    {
        var angle = Math.Atan2(y, x) * (8 / Math.PI);
        return ((int)Math.Round(angle, MidpointRounding.AwayFromZero) + 16) % 16;
    }
}
