using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkinManager.Core;

var originalUiCulture = CultureInfo.CurrentUICulture;
var testRoot = Path.Combine(Path.GetTempPath(), $"SkinManagerLocalization-{Guid.NewGuid():N}");
var settingsPath = Path.Combine(testRoot, "language.json");
Directory.CreateDirectory(testRoot);

try
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
    AppLocalization.Initialize(settingsPath);
    CheckEmbeddedPlaceholderPairs();
    Check(AppLocalization.Preference == AppLanguage.System, "A missing preference should use System.");
    Check(AppLocalization.LanguageCode == "en", "A non-Chinese system UI culture should resolve to English.");

    var changedEvents = 0;
    AppLocalization.LanguageChanged += (_, _) => changedEvents++;
    AppLocalization.SetLanguage(AppLanguage.SimplifiedChinese);
    Check(AppLocalization.Preference == AppLanguage.SimplifiedChinese, "The selected preference should be retained.");
    Check(AppLocalization.LanguageCode == "zh", "Simplified Chinese should resolve to zh.");
    Check(File.Exists(settingsPath), "The selected preference should be saved to the injected path.");
    using (var preference = JsonDocument.Parse(File.ReadAllText(settingsPath)))
    {
        Check(preference.RootElement.GetProperty("Language").GetString() == "SimplifiedChinese",
            "The preference file should contain the selected language.");
    }

    AppLocalization.SetLanguage(AppLanguage.English);
    Check(AppLocalization.IsEnglish && AppLocalization.Culture.Name == "en-US",
        "English should use an English culture for formatting.");
    Check(AppLocalization.Format("已找到 {0} 个 Codex 主实例。", 7) == "Found 7 Codex main instances.",
        "Format should translate a source and preserve its placeholder.");
    Check(AppLocalization.Format("安装包大小：{0:N1}", 1234.5m) == "Installer size: 1,234.5",
        "Format should use the active culture for numeric values.");

    const string userPath = @"C:\Users\Example\My {skins}\Ayanami.png";
    Check(AppLocalization.Get(userPath) == userPath, "Unknown user content should remain unchanged.");
    var formattedPath = AppLocalization.Format("定位失败：{0}", userPath);
    Check(formattedPath.Contains(userPath, StringComparison.Ordinal),
        "Formatting should preserve user-provided path text, including braces.");

    File.WriteAllText(settingsPath, "{\"Language\":\"SimplifiedChinese\"}");
    AppLocalization.Initialize(settingsPath);
    Check(AppLocalization.Preference == AppLanguage.SimplifiedChinese && AppLocalization.LanguageCode == "zh",
        "Initialize should reload the preference from the injected path.");
    Check(changedEvents >= 3, "Preference or effective-language changes should raise LanguageChanged.");

    const string missing = "用户自定义的皮肤标题";
    AppLocalization.SetLanguage(AppLanguage.English);
    Check(AppLocalization.Get(missing) == missing && AppLocalization.MissingSources.Contains(missing),
        "Missing translations should be detectable without changing the source value.");

    AppLocalization.SetLanguage(AppLanguage.System);
    Console.WriteLine("Localization checks passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    try
    {
        AppLocalization.SetLanguage(AppLanguage.System);
    }
    catch
    {
    }

    CultureInfo.CurrentUICulture = originalUiCulture;
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void CheckEmbeddedPlaceholderPairs()
{
    var assembly = typeof(AppLocalization).Assembly;
    foreach (var resourceName in assembly.GetManifestResourceNames()
                 .Where(name => name.Contains("English.", StringComparison.Ordinal) &&
                                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing localization resource: {resourceName}");
        using var document = JsonDocument.Parse(stream);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var sourcePlaceholders = ExtractPlaceholderIndexes(entry.Name);
            var translatedPlaceholders = ExtractPlaceholderIndexes(entry.Value.GetString() ?? string.Empty);
            Check(sourcePlaceholders.SetEquals(translatedPlaceholders),
                $"Placeholder mismatch in {resourceName}: {entry.Name}");
        }
    }
}

static HashSet<int> ExtractPlaceholderIndexes(string value) => Regex.Matches(
        value,
        @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})")
    .Cast<Match>()
    .Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture))
    .ToHashSet();
