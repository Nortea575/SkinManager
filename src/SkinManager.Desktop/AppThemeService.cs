using System.IO;
using System.Text.Json;
using System.Windows;

namespace SkinManager.Desktop;

internal enum AppThemeMode
{
    Night,
    Day
}

internal static class AppThemeService
{
    private static AppThemeMode _mode = AppThemeMode.Night;
    private static string? _settingsPath;
    private static bool _initialized;

    internal static AppThemeMode Mode => _mode;

    internal static event EventHandler? ThemeChanged;

    internal static void Initialize(string? settingsPath = null)
    {
        var nextPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkinManager",
            "app-preferences.json");
        var nextMode = ReadMode(nextPath);
        var changed = _initialized && _mode != nextMode;
        _settingsPath = nextPath;
        _mode = nextMode;
        ApplyPalette();
        _initialized = true;
        if (changed)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    internal static void SetMode(AppThemeMode mode)
    {
        if (!_initialized)
        {
            _initialized = true;
            _settingsPath = null;
        }

        if (_mode == mode)
        {
            return;
        }

        SaveMode(mode);
        _mode = mode;
        ApplyPalette();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static AppThemeMode ReadMode(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Mode", out var modeElement) &&
                    modeElement.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<AppThemeMode>(modeElement.GetString(), ignoreCase: true, out var mode) &&
                    Enum.IsDefined(mode))
                {
                    return mode;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }

        return AppThemeMode.Night;
    }

    private static void SaveMode(AppThemeMode mode)
    {
        if (_settingsPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new ThemePreference(mode.ToString())));
    }

    private static void ApplyPalette()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        void ReplacePalette()
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri($"/SkinManager;component/Themes/Klay.{_mode}.xaml", UriKind.Relative)
            };

            var mergedDictionaries = application.Resources.MergedDictionaries;
            var existingPaletteIndex = -1;
            for (var index = 0; index < mergedDictionaries.Count; index++)
            {
                var source = mergedDictionaries[index].Source?.OriginalString;
                if (source?.Contains("Themes/Klay.Night.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                    source?.Contains("Themes/Klay.Day.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                    source?.Contains("Themes\\Klay.Night.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                    source?.Contains("Themes\\Klay.Day.xaml", StringComparison.OrdinalIgnoreCase) == true)
                {
                    existingPaletteIndex = index;
                    break;
                }
            }

            if (existingPaletteIndex < 0)
            {
                mergedDictionaries.Add(palette);
            }
            else
            {
                mergedDictionaries[existingPaletteIndex] = palette;
            }
        }

        if (application.Dispatcher.CheckAccess())
        {
            ReplacePalette();
        }
        else
        {
            application.Dispatcher.Invoke(ReplacePalette);
        }
    }

    private sealed record ThemePreference(string Mode);
}
