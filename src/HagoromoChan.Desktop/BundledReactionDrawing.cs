using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

// Row-major layout of the hand-drawn 3 x 3 pose atlas. Every cell shares a foot anchor.
internal sealed class BundledReactionDrawing
{
    private enum Pose { Neutral, Up, Down, Left, Right, Blink, Wave, Jump, Concerned }

    private readonly BitmapSource[] _cells;
    private readonly Rect _canvas;
    private readonly Dictionary<(Pose Pose, int Lift), ImageSource> _drawings = new();
    private double _displayScale = 1;
    private Pose _lastLook = Pose.Neutral;

    public BundledReactionDrawing(BitmapSource original)
    {
        _canvas = new Rect(0, 0, original.PixelWidth, original.PixelHeight);
        var uri = new Uri("pack://application:,,,/HagoromoChan.Desktop;component/Assets/MascotReactions.png");
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidDataException("同梱のアクション素材が見つかりません。");
        using var stream = resource.Stream;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var sheet = decoder.Frames[0];
        if (sheet.PixelWidth < 3 || sheet.PixelHeight < 3 || sheet.PixelWidth % 3 != 0 || sheet.PixelHeight % 3 != 0)
            throw new InvalidDataException("同梱のアクション素材を9つの姿勢に分割できません。");

        var w = sheet.PixelWidth / 3;
        var h = sheet.PixelHeight / 3;
        _cells = new BitmapSource[9];
        for (var i = 0; i < _cells.Length; i++)
        {
            var cell = new CroppedBitmap(sheet, new Int32Rect(i % 3 * w, i / 3 * h, w, h));
            cell.Freeze();
            _cells[i] = cell;
        }
    }

    public ImageSource RestingImage => Draw(Pose.Neutral);

    public void SetDisplayScale(double scale)
    {
        if (_displayScale == scale) return;
        _displayScale = scale;
        _drawings.Clear();
    }

    public ImageSource Frame(string action, double progress, double gazeX, double gazeY, long nowMs)
    {
        if (action is "look" or "waiting")
        {
            _lastLook = SelectLook(gazeX, gazeY);
            return Draw(_lastLook);
        }
        _lastLook = Pose.Neutral;

        // A blink is one brief, drawn expression. Keep the eyes open between blinks.
        if (action == "idle")
            return nowMs % 4800 >= 4650 ? Draw(Pose.Blink) : RestingImage;
        if (progress < .09 || progress >= .92)
            return RestingImage;

        var phase = (progress - .09) / .83;
        var arc = Math.Sin(phase * Math.PI);
        return action switch
        {
            "review" => Draw(Pose.Down),
            "failed" => Draw(Pose.Concerned),
            "waving" => Draw(Pose.Wave, (int)Math.Round(2 * arc)),
            "jumping" => Draw(Pose.Jump, (int)Math.Round(12 * arc)),
            _ => RestingImage
        };
    }

    private Pose SelectLook(double x, double y)
    {
        var dx = Math.Abs(x);
        var dy = Math.Abs(y);
        if (Math.Max(dx, dy) < .18) return Pose.Neutral;
        // Close to a diagonal, keep the previous axis until the other direction is clear.
        // This avoids toggling two complete drawings when a target moves by one pixel.
        var horizontal = Math.Abs(dx - dy) < .13 && _lastLook != Pose.Neutral
            ? _lastLook is Pose.Left or Pose.Right
            : dx >= dy;
        if (horizontal) return x < 0 ? Pose.Left : Pose.Right;
        return y < 0 ? Pose.Up : Pose.Down;
    }

    private ImageSource Draw(Pose pose, int lift = 0)
    {
        var key = (pose, lift);
        if (_drawings.TryGetValue(key, out var image)) return image;

        var cell = _cells[(int)pose];
        var scale = Math.Min(1 / _displayScale,
            Math.Min(_canvas.Width / cell.PixelWidth, _canvas.Height / cell.PixelHeight));
        var width = cell.PixelWidth * scale;
        var height = cell.PixelHeight * scale;
        var left = (_canvas.Width - width) / 2;
        var top = Math.Max(0, _canvas.Height - height - lift * _canvas.Height / 200);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, _canvas);
            context.DrawImage(cell, new Rect(left, top, width, height));
        }
        image = new DrawingImage(drawing);
        image.Freeze();
        _drawings.Add(key, image);
        return image;
    }
}
