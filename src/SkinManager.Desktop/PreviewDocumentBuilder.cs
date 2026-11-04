using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SkinManager.Core;

namespace SkinManager.Desktop;

internal sealed record PreviewDocument(
    string Generation,
    string DocumentUri,
    BasePackage BasePackage,
    SkinPackage? Package,
    PreviewOptions Options,
    string AppearancePreference,
    string FixturePage,
    byte[] HtmlBytes);

internal static class PreviewDocumentBuilder
{
    public const string PreviewHost = "codex-preview.invalid";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string ShortViewportHomeLayoutStyle = """
        <style>
        @media (max-height: 380px) {
          html[data-skin-preview-mode="package"] [data-skin-part="home"] > [class*="overflow-y-auto"] {
            justify-content: flex-start !important;
          }
          html[data-skin-preview-mode="package"] [data-skin-part="home"] > [class*="overflow-y-auto"] > div {
            flex: 0 0 auto !important;
            min-height: max-content !important;
          }
        }
        </style>
        """;

    public static PreviewDocument Build(BasePackage basePackage, SkinPackage? package, PreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(basePackage);
        ArgumentNullException.ThrowIfNull(options);

        var generation = Guid.NewGuid().ToString("N");
        var scene = NormalizeScene(options.Scene);
        var fixturePage = scene == "home" ? "home" : "task";
        var appearancePreference = NormalizeAppearance(options.Appearance);
        var initialAppearance = appearancePreference == "dark" ? "dark" : "light";
        var sidebar = options.SidebarVisible ? "open" : "closed";
        var html = StrictUtf8.GetString(basePackage.EntryBytes);
        html = SetRootAttribute(html, "data-theme", initialAppearance);
        html = SetRootAttribute(html, "data-fixture-page", fixturePage);
        html = SetRootAttribute(html, "data-fixture-sidebar", sidebar);
        html = SetRootAttribute(html, "data-fixture-dialog", "none");
        html = SetRootAttribute(html, "data-skin-preview-mode", package is null || options.ShowOriginalBase ? "base" : "package");
        html = SetInactiveFixturePages(html, fixturePage);
        html = EnsureNativeStylesheets(html, basePackage);
        html = EnsurePreviewCascadeLayer(html);
        if (package is not null && !options.ShowOriginalBase)
        {
            html = InsertAfterHeadOpen(html, ShortViewportHomeLayoutStyle);
        }

        html = ReplaceContentSecurityPolicy(html);

        var documentUri = $"https://{PreviewHost}/{generation}/base/{EncodeRelativePath(basePackage.Entry)}" +
                          $"?page={fixturePage}&scene={scene}&sidebar={sidebar}&dialog=none";
        return new PreviewDocument(
            generation,
            documentUri,
            basePackage,
            package,
            options with { Scene = scene, Appearance = appearancePreference },
            appearancePreference,
            fixturePage,
            new UTF8Encoding(false).GetBytes(html));
    }

    public static string NormalizeScene(string? scene) => scene?.Trim().ToLowerInvariant() switch
    {
        "conversation" => "conversation",
        "home" or null or "" => "home",
        _ => throw new InvalidDataException("预览场景必须是 home 或 conversation。")
    };

    public static string NormalizeAppearance(string? appearance) => appearance?.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        "system" or null or "" => "system",
        _ => throw new InvalidDataException("预览外观必须是 system、light 或 dark。")
    };

    public static string EncodeRelativePath(string path) => string.Join('/',
        path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    private static string SetInactiveFixturePages(string html, string activePage)
    {
        var pagePanel = new Regex(
            "<[A-Za-z][^>]*\\bdata-fixture-page-panel\\s*=\\s*(['\"])(?<page>home|task)\\1[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return pagePanel.Replace(html, match =>
        {
            var isActive = match.Groups["page"].Value.Equals(activePage, StringComparison.OrdinalIgnoreCase);
            return SetTagAttribute(match.Value, "data-app-shell-active-page", isActive ? "true" : "false");
        });
    }

    private static string EnsureNativeStylesheets(string html, BasePackage basePackage)
    {
        var existing = Regex.Matches(html, "<link\\b[^>]*\\bhref\\s*=\\s*(['\"])(?<href>.*?)\\1[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => NormalizeHref(match.Groups["href"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingLinks = new StringBuilder();
        foreach (var style in basePackage.Styles)
        {
            var relativePath = RelativeToEntry(basePackage.Entry, style);
            if (existing.Contains(NormalizeHref(relativePath)))
            {
                continue;
            }

            missingLinks.Append("<link rel=\"stylesheet\" href=\"")
                .Append(WebUtility.HtmlEncode(EncodeRelativePath(relativePath)))
                .Append("\">");
            existing.Add(NormalizeHref(relativePath));
        }

        return missingLinks.Length == 0 ? html : InsertBeforeHeadClose(html, missingLinks.ToString());
    }

    private static string ReplaceContentSecurityPolicy(string html)
    {
        const string policy = "default-src 'none'; base-uri 'none'; form-action 'none'; object-src 'none'; connect-src 'none'; script-src 'none'; frame-src 'none'; style-src 'unsafe-inline' https://codex-preview.invalid data:; img-src https://codex-preview.invalid data:; font-src https://codex-preview.invalid data:; media-src https://codex-preview.invalid data:";
        var meta = "<meta http-equiv=\"Content-Security-Policy\" content=\"" + policy + "\">";
        var pattern = "<meta\\b(?=[^>]*\\bhttp-equiv\\s*=\\s*(['\"])Content-Security-Policy\\1)[^>]*>";
        var withoutExistingPolicy = Regex.Replace(html, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return InsertAfterHeadOpen(withoutExistingPolicy, meta);
    }

    private static string EnsurePreviewCascadeLayer(string html) =>
        InsertAfterHeadOpen(html, "<style>@layer skin-manager-native, properties, theme, base, components, utilities;</style>");

    private static string SetRootAttribute(string html, string name, string value)
    {
        var match = Regex.Match(html, "<html\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidDataException("原生基础包 HTML 入口缺少 html 根元素。");
        }

        return html[..match.Index] + SetTagAttribute(match.Value, name, value) + html[(match.Index + match.Length)..];
    }

    private static string SetTagAttribute(string sourceTag, string name, string value)
    {
        var tag = sourceTag;
        var attributePattern = new Regex(
            $"(?<prefix>\\b{Regex.Escape(name)}\\s*=\\s*)(?:(?<quote>['\"])(?<quoted>.*?)\\k<quote>|(?<unquoted>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (attributePattern.IsMatch(tag))
        {
            tag = attributePattern.Replace(tag, attributeMatch =>
            {
                var quote = attributeMatch.Groups["quote"].Value;
                if (quote.Length == 0)
                {
                    quote = "\"";
                }

                return attributeMatch.Groups["prefix"].Value + quote + WebUtility.HtmlEncode(value) + quote;
            }, 1);
        }
        else
        {
            var insertAt = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
            tag = tag.Insert(insertAt, $" {name}=\"{WebUtility.HtmlEncode(value)}\"");
        }

        return tag;
    }

    private static string InsertBeforeHeadClose(string html, string insertion)
    {
        var index = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidDataException("原生基础包 HTML 入口缺少 head 结束标签。");
        }

        return html.Insert(index, insertion);
    }

    private static string InsertAfterHeadOpen(string html, string insertion)
    {
        var match = Regex.Match(html, "<head\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidDataException("原生基础包 HTML 入口缺少 head 元素。");
        }

        return html.Insert(match.Index + match.Length, insertion);
    }

    private static string RelativeToEntry(string entry, string target)
    {
        var entryParts = entry.Split('/');
        var directory = entryParts[..^1];
        var targetParts = target.Split('/');
        var common = 0;
        while (common < directory.Length && common < targetParts.Length &&
               directory[common].Equals(targetParts[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        return string.Join('/', Enumerable.Repeat("..", directory.Length - common).Concat(targetParts.Skip(common)));
    }

    private static string NormalizeHref(string href)
    {
        var value = href.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return value;
        }

        value = value.Split('?', '#')[0].Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
