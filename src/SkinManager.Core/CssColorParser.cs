using System.Globalization;
using System.Text.RegularExpressions;

namespace SkinManager.Core;

public readonly record struct CssRgba(byte Red, byte Green, byte Blue, byte Alpha);

public static class CssColorParser
{
    private const string NumberPattern = @"(?:\d+(?:\.\d*)?|\.\d+)";
    private static readonly Regex RgbPattern = new(
        $@"^rgba?\(\s*(?<red>{NumberPattern})\s*,\s*(?<green>{NumberPattern})\s*,\s*(?<blue>{NumberPattern})(?:\s*,\s*(?<alpha>{NumberPattern}))?\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string? value, out CssRgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var source = value.Trim();
        if (source[0] == '#')
        {
            return TryParseHex(source.AsSpan(1), out color);
        }

        var match = RgbPattern.Match(source);
        if (!match.Success ||
            !TryReadNumber(match.Groups["red"].Value, out var red) ||
            !TryReadNumber(match.Groups["green"].Value, out var green) ||
            !TryReadNumber(match.Groups["blue"].Value, out var blue))
        {
            return false;
        }

        var alpha = 1d;
        if (match.Groups["alpha"].Success && !TryReadNumber(match.Groups["alpha"].Value, out alpha))
        {
            return false;
        }

        color = new CssRgba(
            ToChannel(red, 255),
            ToChannel(green, 255),
            ToChannel(blue, 255),
            ToChannel(alpha, 1));
        return true;
    }

    private static bool TryParseHex(ReadOnlySpan<char> digits, out CssRgba color)
    {
        color = default;
        if (digits.Length is 3 or 4)
        {
            var red = HexNibble(digits[0]);
            var green = HexNibble(digits[1]);
            var blue = HexNibble(digits[2]);
            var alpha = digits.Length == 4 ? HexNibble(digits[3]) : 15;
            if (red < 0 || green < 0 || blue < 0 || alpha < 0)
            {
                return false;
            }

            color = new CssRgba((byte)(red * 17), (byte)(green * 17), (byte)(blue * 17), (byte)(alpha * 17));
            return true;
        }

        if (digits.Length is not (6 or 8) ||
            !TryHexByte(digits[..2], out var redByte) ||
            !TryHexByte(digits.Slice(2, 2), out var greenByte) ||
            !TryHexByte(digits.Slice(4, 2), out var blueByte))
        {
            return false;
        }

        var alphaByte = (byte)255;
        if (digits.Length == 8 && !TryHexByte(digits.Slice(6, 2), out alphaByte))
        {
            return false;
        }

        color = new CssRgba(redByte, greenByte, blueByte, alphaByte);
        return true;
    }

    private static bool TryHexByte(ReadOnlySpan<char> value, out byte result) =>
        byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

    private static int HexNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };

    private static bool TryReadNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    private static byte ToChannel(double value, double maximum) =>
        (byte)Math.Round(Math.Clamp(value, 0, maximum) / maximum * byte.MaxValue, MidpointRounding.AwayFromZero);
}
