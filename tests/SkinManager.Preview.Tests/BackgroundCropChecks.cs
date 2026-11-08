using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkinManager.Desktop;

namespace SkinManager.Preview.Tests;

internal static class BackgroundCropChecks
{
    private static readonly double[] Focuses = [0, 0.25, 0.5, 0.75, 1];

    public static void Run()
    {
        VerifyHorizontalCrop();
        VerifyVerticalCrop();
        Console.WriteLine("PASS 背景裁切焦点与 CSS 百分比位置一致（横图、竖图）");
    }

    private static void VerifyHorizontalCrop()
    {
        const int sourceWidth = 240;
        const int sourceHeight = 160;
        const int cropWidth = 160;
        const int cropHeight = 160;
        var source = CreateCoordinateBitmap(sourceWidth, sourceHeight);
        var availableX = sourceWidth - cropWidth;

        foreach (var focus in Focuses)
        {
            var expectedX = (int)Math.Round(availableX * focus, MidpointRounding.AwayFromZero);
            var crop = PreviewImageCropper.CropToRatio(source, 1, focus, 1);
            Assert(crop.PixelWidth == cropWidth && crop.PixelHeight == cropHeight,
                $"横图焦点 {focus} 裁切尺寸为 {cropWidth}×{cropHeight}");
            AssertPixel(crop, 0, 0, expectedX, 0, $"横图焦点 {focus} 左上边缘");
            AssertPixel(crop, cropWidth - 1, cropHeight - 1, expectedX + cropWidth - 1, cropHeight - 1,
                $"横图焦点 {focus} 右下边缘");
        }
    }

    private static void VerifyVerticalCrop()
    {
        const int sourceWidth = 160;
        const int sourceHeight = 240;
        const int cropWidth = 160;
        const int cropHeight = 160;
        var source = CreateCoordinateBitmap(sourceWidth, sourceHeight);
        var availableY = sourceHeight - cropHeight;

        foreach (var focus in Focuses)
        {
            var expectedY = (int)Math.Round(availableY * focus, MidpointRounding.AwayFromZero);
            var crop = PreviewImageCropper.CropToRatio(source, 1, 1, focus);
            Assert(crop.PixelWidth == cropWidth && crop.PixelHeight == cropHeight,
                $"竖图焦点 {focus} 裁切尺寸为 {cropWidth}×{cropHeight}");
            AssertPixel(crop, 0, 0, 0, expectedY, $"竖图焦点 {focus} 左上边缘");
            AssertPixel(crop, cropWidth - 1, cropHeight - 1, cropWidth - 1, expectedY + cropHeight - 1,
                $"竖图焦点 {focus} 右下边缘");
        }
    }

    private static BitmapSource CreateCoordinateBitmap(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void AssertPixel(BitmapSource bitmap, int x, int y, int sourceX, int sourceY, string label)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        if (pixel[0] != (byte)sourceX || pixel[1] != (byte)sourceY)
        {
            throw new InvalidOperationException(
                $"FAIL {label}: 实际源像素 ({pixel[0]}, {pixel[1]})，预期 ({sourceX}, {sourceY})。");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL 背景裁切检查：{message}。");
        }
    }
}
