using System.Globalization;
using System.Text.RegularExpressions;

namespace SkinManager.Updating;

/// <summary>A small SemVer 2.0 value for release-tag parsing and ordering.</summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string? _prerelease;
    private readonly string? _buildMetadata;

    public SemanticVersion(int major, int minor, int patch, string? prerelease = null, string? buildMetadata = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Semantic version components cannot be negative.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
        _buildMetadata = buildMetadata;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => !string.IsNullOrEmpty(_prerelease);
    public string? Prerelease => _prerelease;
    public string? BuildMetadata => _buildMetadata;
    public Version CoreVersion => new(Major, Minor, Patch);

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (value is null)
        {
            return false;
        }

        var match = Pattern.Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            patch,
            match.Groups[4].Success ? match.Groups[4].Value : null,
            match.Groups[5].Success ? match.Groups[5].Value : null);
        return true;
    }

    public static bool TryParseTag(string? tag, out SemanticVersion version)
    {
        version = default;
        if (tag is null)
        {
            return false;
        }

        var value = tag.StartsWith('v') ? tag[1..] : tag;
        return TryParse(value, out version);
    }

    public int CompareTo(SemanticVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;

        if (!IsPrerelease)
        {
            return other.IsPrerelease ? 1 : 0;
        }

        if (!other.IsPrerelease)
        {
            return -1;
        }

        var left = _prerelease!.Split('.');
        var right = other._prerelease!.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = IsNumericIdentifier(left[index]);
            var rightNumeric = IsNumericIdentifier(right[index]);
            if (leftNumeric && rightNumeric)
            {
                comparison = CompareNumericIdentifiers(left[index], right[index]);
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(left[index], right[index]);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, _prerelease);

    public override string ToString()
    {
        var version = $"{Major}.{Minor}.{Patch}";
        if (IsPrerelease)
        {
            version += "-" + _prerelease;
        }

        if (!string.IsNullOrEmpty(_buildMetadata))
        {
            version += "+" + _buildMetadata;
        }

        return version;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool IsNumericIdentifier(string value) =>
        value.All(character => character is >= '0' and <= '9');

    private static int CompareNumericIdentifiers(string left, string right)
    {
        var comparison = left.Length.CompareTo(right.Length);
        return comparison != 0 ? comparison : string.CompareOrdinal(left, right);
    }
}
