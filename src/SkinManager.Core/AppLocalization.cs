using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace SkinManager.Core;

public static class AppLocalization
{
    private static readonly object StateGate = new();
    private static readonly IReadOnlyDictionary<string, string> EnglishTranslations = LoadEnglishTranslations();
    private static readonly ConcurrentDictionary<string, byte> Missing = new(StringComparer.Ordinal);
    private static LocalizationState _state = new(AppLanguage.System, false, CultureInfo.GetCultureInfo("zh-CN"));
    private static string? _settingsPath;
    private static bool _systemLanguageCaptured;
    private static volatile bool _initialized;

    public static event EventHandler? LanguageChanged;

    public static AppLanguage Preference => Volatile.Read(ref _state).Preference;

    public static CultureInfo Culture => Volatile.Read(ref _state).Culture;

    public static bool IsEnglish => Volatile.Read(ref _state).IsEnglish;

    public static string LanguageCode => IsEnglish ? "en" : "zh";

    public static IReadOnlyCollection<string> MissingSources => Missing.Keys.Order(StringComparer.Ordinal).ToArray();

    public static void Initialize(string? settingsPath = null)
    {
        EventHandler? changedHandler = null;
        lock (StateGate)
        {
            if (!_systemLanguageCaptured)
            {
                _systemLanguageCaptured = true;
                _systemUsesChinese = IsChineseCulture(CultureInfo.CurrentUICulture);
            }

            var nextPath = settingsPath ?? GetDefaultSettingsPath();
            var nextPreference = ReadPreference(nextPath);
            var nextState = CreateState(nextPreference, _systemUsesChinese);
            var previousState = _state;
            var changed = _initialized &&
                          (previousState.Preference != nextState.Preference ||
                           previousState.IsEnglish != nextState.IsEnglish ||
                           !Equals(previousState.Culture, nextState.Culture));

            _settingsPath = nextPath;
            Volatile.Write(ref _state, nextState);
            _initialized = true;
            if (changed)
            {
                changedHandler = LanguageChanged;
            }
        }

        changedHandler?.Invoke(null, EventArgs.Empty);
    }

    public static void SetLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        if (!_initialized)
        {
            Initialize();
        }

        EventHandler? changedHandler = null;
        lock (StateGate)
        {
            var previousState = _state;
            var nextState = CreateState(language, _systemUsesChinese);
            SavePreference(_settingsPath ?? GetDefaultSettingsPath(), language);
            Volatile.Write(ref _state, nextState);

            if (previousState.Preference != nextState.Preference ||
                previousState.IsEnglish != nextState.IsEnglish ||
                !Equals(previousState.Culture, nextState.Culture))
            {
                changedHandler = LanguageChanged;
            }
        }

        changedHandler?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsEnglish)
        {
            return source;
        }

        if (EnglishTranslations.TryGetValue(source, out var translated))
        {
            return translated;
        }

        Missing.TryAdd(source, 0);
        return source;
    }

    public static string Format(string source, params object?[] args) =>
        args.Length == 0 ? Get(source) : string.Format(Culture, Get(source), args);

    private static bool _systemUsesChinese;

    private static string GetDefaultSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkinManager",
        "language.json");

    private static LocalizationState CreateState(AppLanguage preference, bool systemUsesChinese)
    {
        var usesChinese = preference switch
        {
            AppLanguage.SimplifiedChinese => true,
            AppLanguage.English => false,
            _ => systemUsesChinese
        };
        return new LocalizationState(
            preference,
            !usesChinese,
            CultureInfo.GetCultureInfo(usesChinese ? "zh-CN" : "en-US"));
    }

    private static bool IsChineseCulture(CultureInfo culture) =>
        culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static AppLanguage ReadPreference(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var preference = JsonSerializer.Deserialize<LanguagePreference>(File.ReadAllText(path));
                if (preference is not null &&
                    Enum.TryParse<AppLanguage>(preference.Language, ignoreCase: true, out var language) &&
                    Enum.IsDefined(language))
                {
                    return language;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return AppLanguage.System;
    }

    private static void SavePreference(string path, AppLanguage language)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new LanguagePreference(language.ToString())));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> LoadEnglishTranslations()
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(AppLocalization).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.Contains("English.", StringComparison.Ordinal) &&
                           name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (entries is null)
            {
                continue;
            }

            foreach (var (source, translation) in entries)
            {
                translations[source] = translation;
            }
        }

        return translations;
    }

    private sealed record LocalizationState(AppLanguage Preference, bool IsEnglish, CultureInfo Culture);

    private sealed record LanguagePreference(string Language);
}
