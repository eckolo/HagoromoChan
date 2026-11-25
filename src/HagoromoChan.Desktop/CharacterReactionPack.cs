using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HagoromoChan.Desktop;

internal sealed class CharacterReactionPack
{
    private sealed record PackDefinition(int Version, string? BaseArtwork, int CanvasWidth, int CanvasHeight,
        int? AnchorX, int? AnchorY, Dictionary<string, ActionDefinition>? Actions);
    private sealed record ActionDefinition(FrameDefinition[]? Frames);
    private sealed record FrameDefinition(string? File, int X, int Y, int DurationMs);
    private sealed record Frame(BitmapSource Bitmap, int EndMs);
    private readonly Dictionary<string, Frame[]> _actions = new(StringComparer.Ordinal);
    public double AnchorX { get; private init; }
    public double AnchorY { get; private init; }

    public static CharacterReactionPack Load(string directory, bool bundledArtwork, string customArtworkPath)
    {
        var manifestPath = Path.Combine(directory, "pack.json");
        using var manifest = File.OpenRead(manifestPath);
        if (manifest.Length > 256 * 1024) throw new InvalidDataException("pack.jsonが大きすぎます。");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var config = JsonSerializer.Deserialize<PackDefinition>(manifest, options);
        if (config is not { Version: 1, CanvasWidth: >= 1 and <= 4096, CanvasHeight: >= 1 and <= 4096,
                AnchorX: not null, AnchorY: not null, Actions: { Count: >= 1 and <= 32 } }
            || config.AnchorX < 0 || config.AnchorX > config.CanvasWidth
            || config.AnchorY < 0 || config.AnchorY > config.CanvasHeight
            || !config.Actions.ContainsKey("idle"))
            throw new InvalidDataException("版、キャンバス、基準位置、idleの定義を確認してください。");
        if (bundledArtwork)
        {
            if (config.BaseArtwork != "bundled") throw new InvalidDataException("同梱立ち絵に対応しないパックです。");
        }
        else
        {
            using var mascot = File.OpenRead(customArtworkPath);
            if (mascot.Length > 20 * 1024 * 1024 ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(mascot)), config.BaseArtwork,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("mascot.pngとパックの基準画像が一致しません。");
        }

        var pack = new CharacterReactionPack { AnchorX = config.AnchorX.Value, AnchorY = config.AnchorY.Value };
        var images = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        var cells = new Dictionary<(BitmapSource, int, int), BitmapSource>();
        long decodedBytes = 0;
        long checkedPixels = 0;
        foreach (var (key, action) in config.Actions)
        {
            if (string.IsNullOrWhiteSpace(key) || action?.Frames is not { Length: >= 1 and <= 64 } frames
                || (key == "look" && frames.Length != 16))
                throw new InvalidDataException("動作名、有効コマ数、lookの16方向を確認してください。");
            var sequence = new Frame[frames.Length];
            var end = 0;
            for (var index = 0; index < frames.Length; index++)
            {
                var cell = frames[index];
                if (cell is null || cell.DurationMs is < 50 or > 1000
                    || string.IsNullOrWhiteSpace(cell.File) || cell.X < 0 || cell.Y < 0)
                    throw new InvalidDataException("コマの画像・座標・時間が不正です。");
                if (!images.TryGetValue(cell.File, out var sheet))
                {
                    sheet = LoadSheet(directory, cell.File, ref decodedBytes);
                    images.Add(cell.File, sheet);
                }
                if (cell.X > sheet.PixelWidth - config.CanvasWidth || cell.Y > sheet.PixelHeight - config.CanvasHeight)
                    throw new InvalidDataException("シート外のコマが指定されています。");
                var cellKey = (sheet, cell.X, cell.Y);
                if (!cells.TryGetValue(cellKey, out var crop))
                {
                    checkedPixels += (long)config.CanvasWidth * config.CanvasHeight;
                    if (checkedPixels > 32 * 1024 * 1024) throw new InvalidDataException("コマの総画素数が上限を超えます。");
                    crop = new CroppedBitmap(sheet, new Int32Rect(cell.X, cell.Y, config.CanvasWidth, config.CanvasHeight));
                    CheckTransparency(crop);
                    crop.Freeze();
                    cells.Add(cellKey, crop);
                }
                end += cell.DurationMs;
                sequence[index] = new Frame(crop, end);
            }
            pack._actions.Add(key, sequence);
        }
        return pack;
    }

    private static void CheckTransparency(BitmapSource cell)
    {
        var bitmap = new FormatConvertedBitmap(cell, PixelFormats.Pbgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var row = new byte[stride];
        bool clear = false, visible = false;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, 1), row, stride, 0);
            for (var x = 3; x < stride; x += 4)
            {
                clear |= row[x] == 0;
                visible |= row[x] > 0;
            }
            if (clear && visible) return;
        }
        throw new InvalidDataException("各コマにキャラクターと透明な背景が必要です。");
    }

    private static BitmapSource LoadSheet(string directory, string relative, ref long decodedBytes)
    {
        var parts = relative.Split(['/', '\\']);
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            || !string.Equals(Path.GetExtension(relative), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("パック内の相対PNGを指定してください。");
        var path = directory;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("パック外を参照するリンクは使用できません。");
        }
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (stream.Length > 32 * 1024 * 1024 || !header[..8].SequenceEqual(signature)
            || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("有効なPNGを指定してください。");
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        if (width is < 1 or > 8192 || height is < 1 or > 8192)
            throw new InvalidDataException("シートの寸法が範囲外です。");
        // WIC can decode 16-bit RGBA PNGs at eight bytes per pixel.
        decodedBytes += (long)width * height * 8;
        if (decodedBytes > 128L * 1024 * 1024) throw new InvalidDataException("パックの展開サイズが上限を超えます。");
        stream.Position = 0;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var sheet = decoder.Frames[0];
        if (sheet.PixelWidth != width || sheet.PixelHeight != height)
            throw new InvalidDataException("シート寸法が一致しません。");
        sheet.Freeze();
        return sheet;
    }

    public BitmapSource Select(string action, double progress, double gazeX, double gazeY)
    {
        // A legacy waiting sequence is a timed loop; look is a sheet of 16 directions.
        if (action == "thinking" && !_actions.ContainsKey("thinking"))
            action = _actions.ContainsKey("waiting") ? "waiting" : "look";
        else if (action == "waiting" && _actions.ContainsKey("look"))
            action = "look";
        if (action == "look" && _actions.TryGetValue("look", out var directions))
            return directions[CharacterReactionArtwork.LookDirection(gazeX, gazeY)].Bitmap;
        if (action == "frozen") return _actions["idle"][0].Bitmap;
        if (!_actions.TryGetValue(action, out var frames)) frames = _actions["idle"];
        var at = progress * frames[^1].EndMs;
        foreach (var frame in frames)
            if (at < frame.EndMs) return frame.Bitmap;
        return frames[^1].Bitmap;
    }
}
