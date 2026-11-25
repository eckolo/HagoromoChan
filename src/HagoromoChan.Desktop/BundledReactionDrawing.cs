using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

// The first nine poses are stored row-major in the atlas. All poses share a foot anchor.
internal sealed class BundledReactionDrawing
{
    private enum Pose { Neutral, Up, Down, Left, Right, Blink, Wave, Jump, Concerned, Thinking }
    private const int ThinkingSteps = 48;

    private readonly BitmapSource[] _cells;
    private readonly Rect _canvas;
    private readonly Dictionary<(Pose Pose, int Step), ImageSource> _drawings = new();
    private double _displayScale = 1;
    private Pose _lastLook = Pose.Neutral;

    public BundledReactionDrawing(BitmapSource original)
    {
        _canvas = new Rect(0, 0, original.PixelWidth, original.PixelHeight);
        var sheet = LoadBitmap("MascotReactions.png");
        if (sheet.PixelWidth < 3 || sheet.PixelHeight < 3 || sheet.PixelWidth % 3 != 0 || sheet.PixelHeight % 3 != 0)
            throw new InvalidDataException("同梱のアクション素材を9つの姿勢に分割できません。");

        var w = sheet.PixelWidth / 3;
        var h = sheet.PixelHeight / 3;
        _cells = new BitmapSource[(int)Pose.Thinking + 1];
        for (var i = 0; i < 9; i++)
        {
            var cell = new CroppedBitmap(sheet, new Int32Rect(i % 3 * w, i / 3 * h, w, h));
            cell.Freeze();
            _cells[i] = cell;
        }
        var thinking = LoadBitmap("MascotThinking.png");
        if (thinking.PixelWidth != w || thinking.PixelHeight != h)
            throw new InvalidDataException("思案中の立ち絵のサイズが他の姿勢と一致しません。");
        _cells[(int)Pose.Thinking] = thinking;
    }

    private static BitmapSource LoadBitmap(string name)
    {
        var uri = new Uri($"pack://application:,,,/HagoromoChan.Desktop;component/Assets/{name}");
        var resource = Application.GetResourceStream(uri)
            ?? throw new InvalidDataException($"同梱のアクション素材 {name} が見つかりません。");
        using var stream = resource.Stream;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        bitmap.Freeze();
        return bitmap;
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

        if (action == "thinking")
            return Draw(Pose.Thinking, (int)(progress * ThinkingSteps) % ThinkingSteps);

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

    private ImageSource Draw(Pose pose, int step = 0)
    {
        var key = (pose, step);
        if (_drawings.TryGetValue(key, out var image)) return image;

        var cell = _cells[(int)pose];
        var scale = Math.Min(1 / _displayScale,
            Math.Min(_canvas.Width / cell.PixelWidth, _canvas.Height / cell.PixelHeight));
        var width = cell.PixelWidth * scale;
        var height = cell.PixelHeight * scale;
        var left = (_canvas.Width - width) / 2;
        var lift = pose == Pose.Thinking ? 0 : step * _canvas.Height / 200;
        var top = Math.Max(0, _canvas.Height - height - lift);
        var drawing = new DrawingGroup();
        if (pose == Pose.Thinking)
            drawing.ClipGeometry = new RectangleGeometry(_canvas);
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, _canvas);
            if (pose == Pose.Thinking)
            {
                // Keep both soles on the shared baseline while the shoulders breathe and sway.
                var angle = step * Math.Tau / ThinkingSteps;
                var sway = .006 * Math.Sin(angle);
                var breath = .004 * (1 - Math.Cos(angle));
                var footY = top + height * 360 / 368;
                context.PushTransform(new MatrixTransform(1, 0, sway, 1 - breath, -sway * footY, breath * footY));
            }
            context.DrawImage(cell, new Rect(left, top, width, height));
            if (pose == Pose.Thinking) context.Pop();
        }
        image = new DrawingImage(drawing);
        image.Freeze();
        _drawings.Add(key, image);
        return image;
    }
}
