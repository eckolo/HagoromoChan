using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

// The first nine poses are stored row-major in the atlas. All poses share a foot anchor.
internal sealed class BundledReactionDrawing
{
    private enum Pose { Neutral, Up, Down, Left, Right, Blink, Wave, Jump, Concerned, Thinking, Unread }
    // The four cells of the unread-eye atlas are ordered left, right, up, down.
    private enum UnreadGaze { Front, Left, Right, Up, Down }
    private const int ThinkingSteps = 48;

    private readonly BitmapSource[] _cells;
    private readonly BitmapSource _thinkingHead;
    private readonly BitmapSource _thinkingHand;
    private readonly BitmapSource[] _unreadEyes = new BitmapSource[4];
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
        _cells = new BitmapSource[(int)Pose.Unread + 1];
        for (var i = 0; i < 9; i++)
        {
            var cell = new CroppedBitmap(sheet, new Int32Rect(i % 3 * w, i / 3 * h, w, h));
            cell.Freeze();
            _cells[i] = cell;
        }
        BitmapSource LoadLayer(string name, int columns = 1, int rows = 1)
        {
            var bitmap = LoadBitmap(name);
            if (bitmap.PixelWidth != w * columns || bitmap.PixelHeight != h * rows)
                throw new InvalidDataException($"立ち絵 {name} のサイズが他の姿勢と一致しません。");
            return bitmap;
        }

        _cells[(int)Pose.Thinking] = LoadLayer("MascotThinkingBody.png");
        _thinkingHead = LoadLayer("MascotThinkingHead.png");
        _thinkingHand = LoadLayer("MascotThinkingHand.png");
        _cells[(int)Pose.Unread] = LoadLayer("MascotUnread.png");
        var eyes = LoadLayer("MascotUnreadEyes.png", 2, 2);
        for (var i = 0; i < _unreadEyes.Length; i++)
        {
            var eye = new CroppedBitmap(eyes, new Int32Rect(i % 2 * w, i / 2 * h, w, h));
            eye.Freeze();
            _unreadEyes[i] = eye;
        }
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
        if (action == "unread")
        {
            // The last second of the seven-second loop is a brief glance at the balloon.
            var gaze = UnreadGaze.Front;
            if (CharacterReactionState.ShowsUnreadGlance(progress) && (gazeX != 0 || gazeY != 0))
                gaze = Math.Abs(gazeX) >= Math.Abs(gazeY)
                    ? (gazeX < 0 ? UnreadGaze.Left : UnreadGaze.Right)
                    : (gazeY < 0 ? UnreadGaze.Up : UnreadGaze.Down);
            return Draw(Pose.Unread, (int)gaze);
        }

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
        var lift = pose is Pose.Jump or Pose.Wave ? step * _canvas.Height / 200 : 0;
        var top = Math.Max(0, _canvas.Height - height - lift);
        var drawing = new DrawingGroup();
        if (pose == Pose.Thinking)
            drawing.ClipGeometry = new RectangleGeometry(_canvas);
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, _canvas);
            var bounds = new Rect(left, top, width, height);
            context.DrawImage(cell, bounds);
            if (pose == Pose.Thinking)
                DrawThinkingLayers(context, bounds, step);
            else if (pose == Pose.Unread && step != (int)UnreadGaze.Front)
                context.DrawImage(_unreadEyes[step - 1], bounds);
        }
        image = new DrawingImage(drawing);
        image.Freeze();
        _drawings.Add(key, image);
        return image;
    }

    private void DrawThinkingLayers(DrawingContext context, Rect bounds, int step)
    {
        // Rest through phase 4, tilt through 20, hold through 28, and return by 44.
        var ramp = Math.Clamp(Math.Min(step - 4, 44 - step) / 16.0, 0, 1);
        var degrees = -5.5 * (1 - Math.Cos(Math.PI * ramp)) / 2;

        // Neck pivot and chin contact in the 288 x 368 master artwork.
        var neck = new Point(bounds.Left + bounds.Width * 145 / 288, bounds.Top + bounds.Height * 188 / 368);
        var chin = new Point(bounds.Left + bounds.Width * 138 / 288, bounds.Top + bounds.Height * 181 / 368);
        var tilt = new RotateTransform(degrees, neck.X, neck.Y);
        context.PushTransform(tilt);
        context.DrawImage(_thinkingHead, bounds);
        context.Pop();

        // Track only the chin's displacement. The cuff stays within its overlap with the sleeve.
        var shift = tilt.Transform(chin) - chin;
        context.PushTransform(new TranslateTransform(shift.X, shift.Y));
        context.DrawImage(_thinkingHand, bounds);
        context.Pop();
    }
}
