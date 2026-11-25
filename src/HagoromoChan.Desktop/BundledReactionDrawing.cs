using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

// Landmark coordinates belong exclusively to Assets/Mascot.png (530 x 780).
// Replace just the affected rectangle. The unchanged artwork and generated poses are shared.
internal sealed class BundledReactionDrawing
{
    private static readonly Int32Rect Eyes = new(158, 224, 249, 138);
    private static readonly Int32Rect Sleeve = new(0, 405, 245, 304);
    private readonly BitmapSource _original;
    private readonly byte[] _pixels;
    private readonly Rect _canvas;
    private readonly Dictionary<(string, int, int), ImageSource> _poses = new();

    public BundledReactionDrawing(BitmapSource original)
    {
        _original = original;
        var converted = new FormatConvertedBitmap(original, PixelFormats.Pbgra32, null, 0);
        _pixels = new byte[original.PixelWidth * original.PixelHeight * 4];
        converted.CopyPixels(_pixels, original.PixelWidth * 4, 0);
        _canvas = new Rect(0, 0, original.PixelWidth, original.PixelHeight);
    }

    public ImageSource Frame(string action, double progress, double gazeX, double gazeY)
    {
        // Finite pose indices bound the cache, including arbitrarily many target window positions.
        if (action is "look" or "waiting")
        {
            int x = (int)Math.Round(3 * gazeX), y = (int)Math.Round(3 * gazeY);
            if (x == 0 && y == 0) return _original;
            return Cached("look", x, y, () => EyesLooking(x / 3d, y / 3d, 0));
        }
        if (action == "idle")
        {
            // The eyelids close and reopen near the end of the idle cycle.
            var closure = Math.Max(0, 1 - Math.Abs(progress - .8) / .12);
            int blink = (int)Math.Round(4 * closure);
            return blink == 0 ? _original : Cached("blink", blink, 0, () => EyesLooking(0, 0, blink / 4d));
        }
        var index = (int)Math.Round(12 * progress);
        if (index == 0 || index == 12 || action is not ("review" or "waving" or "jumping" or "failed"))
            return _original;
        return Cached(action, index, 0, () => DrawAction(action, index / 12d));
    }

    private ImageSource Cached(string action, int x, int y, Func<ImageSource> draw)
    {
        var key = (action, x, y);
        if (!_poses.TryGetValue(key, out var image))
        {
            image = draw();
            _poses.Add(key, image);
        }
        return image;
    }

    private ImageSource DrawAction(string action, double t)
    {
        var envelope = Math.Sin(Math.PI * t);
        if (action == "waving")
        {
            // Rotate the sleeve about the shoulder. The lapel at the centre remains fixed.
            var angle = (.38 + .11 * Math.Sin(4 * Math.PI * t)) * envelope;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            return WithPatch(Sleeve, (x, y) =>
            {
                var weight = (1 - Smooth(143, 203, x)) * Smooth(422, 484, y) * (1 - Smooth(590, 662, y));
                var dx = x - 192;
                var dy = y - 468;
                return (x + weight * (dx * (cos - 1) + dy * sin),
                    y + weight * (dy * (cos - 1) - dx * sin));
            });
        }
        if (action == "jumping")
        {
            // The sprite fills its canvas at rest. Anticipation lowers the head first;
            // the airborne pose then lifts both feet while keeping the topknot in frame.
            var anticipation = t < .25 ? Math.Sin(Math.PI * t / .25) :
                t > .76 ? .65 * Math.Sin(Math.PI * (t - .76) / .24) : 0;
            var lift = t is > .2 and < .83 ? Math.Sin(Math.PI * (t - .2) / .63) : 0;
            return InsetPose(_original, .022 * lift, .075 * anticipation, .095 * lift);
        }
        var eyesDown = EyesLooking(0, envelope, 0);
        return action == "failed"
            ? InsetPose(eyesDown, .012 * envelope, .065 * envelope, 0)
            : InsetPose(eyesDown, 0, .028 * envelope, 0);
    }

    private ImageSource InsetPose(ImageSource source, double insetX, double insetTop, double insetBottom)
    {
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(_canvas) };
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, _canvas);
            context.DrawImage(source, new Rect(_canvas.Width * insetX, _canvas.Height * insetTop,
                _canvas.Width * (1 - 2 * insetX), _canvas.Height * (1 - insetTop - insetBottom)));
        }
        return Freeze(drawing);
    }

    private ImageSource EyesLooking(double gazeX, double gazeY, double blink)
    {
        var pose = WithPatch(Eyes, (x, y) =>
        {
            var eyeX = x < 281 ? 215 : 352;
            var across = (x - eyeX) / 44d;
            var horizontalWeight = 1 - Smooth(.64, 1, Math.Abs(across));
            if (horizontalWeight <= 0) return (x, y);
            if (blink > 0)
            {
                // Move the two lids toward a curved lash line and stretch adjacent skin
                // into the uncovered region, keeping the hair, nose and cheeks anchored.
                var curve = 1 - across * across;
                var top = 273 - 14 * curve;
                var bottom = 302 + 26 * curve;
                var line = 296 + 7 * curve;
                var close = .96 * blink * horizontalWeight;
                var upper = top + (line - top) * close;
                var lower = bottom + (line - bottom) * close;
                const double brow = 228, cheek = 352;
                if (y <= brow || y >= cheek) return (x, y);
                var sourceY = y < upper ? brow + (y - brow) * (top - brow) / (upper - brow) :
                    y < lower ? top + (y - upper) * (bottom - top) / (lower - upper) :
                    bottom + (y - lower) * (cheek - bottom) / (cheek - lower);
                return (x, sourceY);
            }
            // Displace the irises inside the original lids. Feather to zero at the sclera
            // and lash edges so the hair ornaments and face outline never follow the gaze.
            var centreX = x < 281 ? 222 : 344;
            var rx = (x - centreX) / 38d;
            var ry = (y - 296) / 34d;
            var radial = rx * rx + ry * ry;
            var weight = 1 - Smooth(.18, 1.05, radial);
            return (x - gazeX * 12 * weight, y - gazeY * 11 * weight);
        });
        if (blink < .75) return pose;
        var lashes = new StreamGeometry();
        using (var g = lashes.Open())
        {
            g.BeginFigure(new Point(185, 298), false, false);
            g.QuadraticBezierTo(new Point(211, 311), new Point(243, 299), true, true);
            g.BeginFigure(new Point(322, 299), false, false);
            g.QuadraticBezierTo(new Point(353, 311), new Point(383, 298), true, true);
        }
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawImage(pose, _canvas);
            var stroke = new Pen(new SolidColorBrush(Color.FromRgb(91, 50, 37)), blink * 2.6)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, stroke, lashes);
        }
        return Freeze(drawing);
    }

    // Samples premultiplied pixels to preserve the transparent edges of the lifted sleeve.
    private ImageSource WithPatch(Int32Rect patch, Func<int, int, (double X, double Y)> inverse)
    {
        var stride = patch.Width * 4;
        var pixels = new byte[stride * patch.Height];
        int width = _original.PixelWidth, height = _original.PixelHeight;
        for (int row = 0; row < patch.Height; row++)
        for (int col = 0; col < patch.Width; col++)
        {
            int x = patch.X + col, y = patch.Y + row;
            var (sx, sy) = inverse(x, y);
            sx = Math.Clamp(sx, 0, width - 1);
            sy = Math.Clamp(sy, 0, height - 1);
            int left = (int)sx, top = (int)sy;
            var a = (top * width + left) * 4;
            var target = row * stride + col * 4;
            var fx = sx - left;
            var fy = sy - top;
            if (fx == 0 && fy == 0)
            {
                _pixels.AsSpan(a, 4).CopyTo(pixels.AsSpan(target, 4));
                continue;
            }
            var b = left < width - 1 ? a + 4 : a;
            var down = top < height - 1 ? width * 4 : 0;
            for (var channel = 0; channel < 4; channel++)
            {
                var upper = _pixels[a + channel] * (1 - fx) + _pixels[b + channel] * fx;
                var lower = _pixels[a + down + channel] * (1 - fx) + _pixels[b + down + channel] * fx;
                pixels[target + channel] = (byte)Math.Round(upper * (1 - fy) + lower * fy);
            }
        }
        if (patch == Sleeve)
        {
            // This patch crosses the transparent outline. Composing one premultiplied
            // surface preserves clear pixels vacated by the moving sleeve, even under DPI scaling.
            var composite = (byte[])_pixels.Clone();
            for (var row = 0; row < patch.Height; row++)
                Buffer.BlockCopy(pixels, row * stride, composite, ((patch.Y + row) * width + patch.X) * 4, stride);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, composite, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        var patchBitmap = BitmapSource.Create(patch.Width, patch.Height, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        patchBitmap.Freeze();

        var rect = new Rect(patch.X, patch.Y, patch.Width, patch.Height);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawImage(_original, _canvas);
            // The rectangular face region is opaque in the bundled artwork.
            context.DrawImage(patchBitmap, rect);
        }
        return Freeze(drawing);
    }

    private static DrawingImage Freeze(DrawingGroup drawing)
    {
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static double Smooth(double start, double end, double value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
