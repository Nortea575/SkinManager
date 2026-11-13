using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Core;

namespace SkinManager.Desktop;

internal static class PreviewImageCropper
{
    public static BitmapSource CreateThumbnail(
        byte[] bytes,
        double targetWidthDip,
        double targetHeightDip,
        double dpiScaleX,
        double dpiScaleY,
        double focusX,
        double focusY)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || !double.IsFinite(targetWidthDip) || targetWidthDip <= 0 ||
            !double.IsFinite(targetHeightDip) || targetHeightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidthDip), AppLocalization.Get("缩略图尺寸必须大于零，图片数据不能为空。 "));
        }

        dpiScaleX = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1;
        dpiScaleY = double.IsFinite(dpiScaleY) && dpiScaleY > 0 ? dpiScaleY : 1;
        var targetWidth = Math.Max(1, (int)Math.Ceiling(targetWidthDip * dpiScaleX));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(targetHeightDip * dpiScaleY));

        var dimensions = ReadDimensions(bytes);
        var sourceWidth = dimensions.Width;
        var sourceHeight = dimensions.Height;

        var scale = Math.Min(1d, Math.Max(targetWidth / (double)sourceWidth, targetHeight / (double)sourceHeight));
        var decodeWidth = Math.Clamp((int)Math.Ceiling(sourceWidth * scale), 1, sourceWidth);
        var bitmap = IsGif(bytes)
            ? DecodeGifFirstFrame(bytes, SkinPackageReader.ReadGifInfo(bytes), decodeWidth)
            : DecodeBitmap(bytes, decodeWidth);

        return CropToRatio(bitmap, targetWidth / (double)targetHeight, focusX, focusY);
    }

    public static BitmapSource Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (IsGif(bytes))
        {
            var info = SkinPackageReader.ReadGifInfo(bytes);
            return DecodeGifFirstFrame(bytes, info, info.Width);
        }

        return DecodeBitmap(bytes, decodePixelWidth: 0);
    }

    private static BitmapSource DecodeBitmap(byte[] bytes, int decodePixelWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (decodePixelWidth > 0)
        {
            bitmap.DecodePixelWidth = decodePixelWidth;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static (int Width, int Height) ReadDimensions(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new InvalidDataException(AppLocalization.Get("背景图片数据不能为空。 "));
        }

        if (IsGif(bytes))
        {
            var gif = SkinPackageReader.ReadGifInfo(bytes);
            return (gif.Width, gif.Height);
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException(AppLocalization.Get("背景图片不包含可解码的图像帧。 "));
        }

        var width = decoder.Frames[0].PixelWidth;
        var height = decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(AppLocalization.Get("背景图片尺寸无效。 "));
        }

        return (width, height);
    }

    private static BitmapSource DecodeGifFirstFrame(byte[] bytes, GifImageInfo info, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new GifBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException(AppLocalization.Get("GIF 背景图片不包含可解码的图像帧。 "));
        }

        var frame = decoder.Frames[0];
        var scale = Math.Clamp(decodeWidth / (double)info.Width, 0.0001, 1d);
        var targetWidth = Math.Max(1, (int)Math.Ceiling(info.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(info.Height * scale));
        var alreadyLogicalScreen = frame.PixelWidth == info.Width && frame.PixelHeight == info.Height;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            var backgroundAlpha = info.HasGlobalColorTable &&
                                  !(info.FirstFrameHasTransparency &&
                                    info.FirstFrameTransparentIndex == info.BackgroundColorIndex)
                ? byte.MaxValue
                : (byte)0;
            var background = new SolidColorBrush(Color.FromArgb(
                backgroundAlpha,
                info.BackgroundRed,
                info.BackgroundGreen,
                info.BackgroundBlue));
            background.Freeze();
            context.DrawRectangle(background, null, new Rect(0, 0, info.Width, info.Height));
            var destination = alreadyLogicalScreen
                ? new Rect(0, 0, info.Width, info.Height)
                : new Rect(info.FirstFrameLeft, info.FirstFrameTop, frame.PixelWidth, frame.PixelHeight);
            context.DrawImage(frame, destination);
            context.Pop();
        }

        var canvas = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        canvas.Render(visual);
        canvas.Freeze();
        return canvas;
    }

    private static bool IsGif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));

    public static BitmapSource CropToRatio(BitmapSource source, double ratio, double focusX, double focusY)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), AppLocalization.Get("裁切比例必须大于零。 "));
        }

        var sourceWidth = source.PixelWidth;
        var sourceHeight = source.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new InvalidDataException(AppLocalization.Get("背景图片尺寸无效。 "));
        }

        var sourceRatio = (double)sourceWidth / sourceHeight;
        var cropWidth = sourceWidth;
        var cropHeight = sourceHeight;
        if (sourceRatio > ratio)
        {
            cropWidth = Math.Clamp((int)Math.Floor(sourceHeight * ratio), 1, sourceWidth);
        }
        else if (sourceRatio < ratio)
        {
            cropHeight = Math.Clamp((int)Math.Floor(sourceWidth / ratio), 1, sourceHeight);
        }

        focusX = double.IsFinite(focusX) ? Math.Clamp(focusX, 0, 1) : 0.5;
        focusY = double.IsFinite(focusY) ? Math.Clamp(focusY, 0, 1) : 0.5;
        var x = (int)Math.Round((sourceWidth - cropWidth) * focusX, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round((sourceHeight - cropHeight) * focusY, MidpointRounding.AwayFromZero);
        x = Math.Clamp(x, 0, sourceWidth - cropWidth);
        y = Math.Clamp(y, 0, sourceHeight - cropHeight);

        var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(x, y, cropWidth, cropHeight));
        var independent = new WriteableBitmap(cropped);
        independent.Freeze();
        return independent;
    }
}
