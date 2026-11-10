using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace SkinManager.Desktop;

internal sealed record ChangelogEntry(string Version, string DateLabel, string Body);

internal static class ChangelogReader
{
    internal const string EmbeddedResourceName = "SkinManager.Desktop.Resources.CHANGELOG.md";

    private static readonly Regex ReleaseHeading = new(
        @"^##\s+(?<version>\d+(?:\.\d+){2,3}(?:-[0-9A-Za-z.-]+)?)(?:\s*[·—–-]\s*(?<date>\d{4}-\d{2}-\d{2}))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static IReadOnlyList<ChangelogEntry> ReadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(ChangelogReader).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
                           ?? throw new InvalidDataException($"找不到内嵌更新日志资源：{EmbeddedResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    internal static IReadOnlyList<ChangelogEntry> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var releases = new List<ChangelogEntry>();
        string? version = null;
        var dateLabel = string.Empty;
        var body = new StringBuilder();

        void AppendCurrentRelease()
        {
            if (version is null)
            {
                return;
            }

            releases.Add(new ChangelogEntry(version, dateLabel, FormatBody(body.ToString())));
        }

        using var lines = new StringReader(markdown);
        while (lines.ReadLine() is { } line)
        {
            var match = ReleaseHeading.Match(line.Trim());
            if (match.Success)
            {
                AppendCurrentRelease();
                version = match.Groups["version"].Value;
                var date = match.Groups["date"].Value;
                dateLabel = DateOnly.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate)
                    ? parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : string.Empty;
                body.Clear();
            }
            else if (version is not null)
            {
                body.AppendLine(line);
            }
        }

        AppendCurrentRelease();
        return releases.AsReadOnly();
    }

    private static string FormatBody(string markdownBody)
    {
        var formattedLines = new List<string>();
        foreach (var rawLine in markdownBody.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                if (formattedLines.Count > 0 && formattedLines[^1].Length > 0)
                {
                    formattedLines.Add(string.Empty);
                }

                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                line = $"•  {line[2..].Trim()}";
            }

            formattedLines.Add(line.Replace("**", string.Empty, StringComparison.Ordinal));
        }

        while (formattedLines.Count > 0 && formattedLines[^1].Length == 0)
        {
            formattedLines.RemoveAt(formattedLines.Count - 1);
        }

        return string.Join(Environment.NewLine, formattedLines);
    }
}
