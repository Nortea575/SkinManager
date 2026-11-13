using SkinManager.Core;

namespace SkinManager.Market;

public static class MarketLanguageUri
{
    private const string SiteHost = "blog.ancimen.top";

    public static Uri ForPublicRest(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !(uri.AbsolutePath.StartsWith("/wp-json/", StringComparison.OrdinalIgnoreCase) ||
              ParseQuery(uri.Query).Any(pair => pair.Key.Equals("rest_route", StringComparison.OrdinalIgnoreCase))))
        {
            return uri;
        }

        return AddLanguage(uri, includeWordPressLoginLanguage: false);
    }

    public static Uri ForLogin(Uri uri) => AddLanguage(uri, includeWordPressLoginLanguage: true);

    public static Uri ForMarketPage(Uri uri) => AddLanguage(uri, includeWordPressLoginLanguage: false);

    private static Uri AddLanguage(Uri uri, bool includeWordPressLoginLanguage)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IdnHost.Equals(SiteHost, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var language = AppLocalization.LanguageCode;
        var query = ParseQuery(uri.Query)
            .Where(pair => !pair.Key.Equals("lang", StringComparison.OrdinalIgnoreCase) &&
                           !pair.Key.Equals("wp_lang", StringComparison.OrdinalIgnoreCase))
            .ToList();
        query.Add(new KeyValuePair<string, string>("lang", language));
        if (includeWordPressLoginLanguage && uri.AbsolutePath.Equals("/wp-login.php", StringComparison.OrdinalIgnoreCase))
        {
            query.Add(new KeyValuePair<string, string>("wp_lang", language == "en" ? "en_US" : "zh_CN"));
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };
        return builder.Uri;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            yield return new KeyValuePair<string, string>(
                Uri.UnescapeDataString(key.Replace('+', ' ')),
                Uri.UnescapeDataString(value.Replace('+', ' ')));
        }
    }
}
