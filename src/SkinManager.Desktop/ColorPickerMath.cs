using SkinManager.Core;
using System.Windows.Media;

namespace SkinManager.Desktop;

internal readonly record struct HsvColor(double Hue, double Saturation, double Value);

internal static class ColorPickerMath
{
    public static HsvColor FromRgba(CssRgba color, double retainedHue = 0)
    {
        var red = color.Red / 255d;
        var green = color.Green / 255d;
        var blue = color.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum;

        if (delta < 1e-10)
        {
            return new HsvColor(WrapHue(retainedHue), 0, maximum);
        }

        var hue = maximum == red
            ? 60 * (((green - blue) / delta) % 6)
            : maximum == green
                ? 60 * (((blue - red) / delta) + 2)
                : 60 * (((red - green) / delta) + 4);
        var saturation = maximum <= 0 ? 0 : delta / maximum;
        return new HsvColor(WrapHue(hue), Math.Clamp(saturation, 0, 1), maximum);
    }

    public static CssRgba ToRgba(HsvColor hsv, byte alpha)
    {
        var hue = WrapHue(hsv.Hue);
        var saturation = Math.Clamp(hsv.Saturation, 0, 1);
        var value = Math.Clamp(hsv.Value, 0, 1);
        var chroma = value * saturation;
        var segment = hue / 60;
        var secondary = chroma * (1 - Math.Abs(segment % 2 - 1));
        var match = value - chroma;
        var (red, green, blue) = segment switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };

        return new CssRgba(ToByte(red + match), ToByte(green + match), ToByte(blue + match), alpha);
    }

    public static Color ToMediaColor(CssRgba color) =>
        Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    public static string ToCssHex(CssRgba color) => color.Alpha == byte.MaxValue
        ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
        : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2}";

    public static double WrapHue(double hue)
    {
        if (!double.IsFinite(hue))
        {
            return 0;
        }

        var wrapped = hue % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
}
