using System.Text.Json.Nodes;

namespace SkinManager.Core;

/// <summary>Creates and updates the small foundation shared by visual skin workflows.</summary>
public static class SkinCreation
{
    private sealed record Palette(string Window, string PrimaryText, string Accent, double FillOpacity);

    private static readonly Palette LightPalette = new("#F7F9FC", "#17202B", "#2563EB", 0.28);
    private static readonly Palette DarkPalette = new("#202735", "#E6EBF3", "#9AB3DF", 0.40);

    /// <summary>Creates a usable light or dark skin draft with renderer-derived colors.</summary>
    public static SkinDraft Create(string id, string name, SkinAppearance appearance = SkinAppearance.Light)
    {
        var palette = GetPalette(appearance);
        var draft = SkinDraft.CreateNew(id, name);
        draft.Manifest["appearance"] = ToManifestAppearance(appearance);
        draft.Manifest["tokens"] = new JsonObject
        {
            ["color.surface.window"] = palette.Window,
            ["color.text.primary"] = palette.PrimaryText,
            ["color.accent"] = palette.Accent,
            ["material.fillOpacity"] = palette.FillOpacity,
            ["radius.control"] = 10,
            ["radius.panel"] = 16
        };
        draft.Manifest["background"] = new JsonObject
        {
            ["color"] = "{color.surface.window}",
            ["overlayColor"] = "{color.surface.window}",
            ["overlayOpacity"] = GetDefaultOverlayOpacity(appearance)
        };

        // Fail early for IDs or names that cannot produce a valid package.
        _ = draft.BuildPackage();
        return draft;
    }

    /// <summary>
    /// Changes the selected appearance in place. Main colors follow the built-in palette
    /// only while they still match the previous built-in values.
    /// </summary>
    public static SkinDraft ChangeAppearance(SkinDraft draft, SkinAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var targetPalette = GetPalette(appearance);
        var previousAppearance = ReadAppearance(draft.Manifest["appearance"]);
        if (previousAppearance is SkinAppearance.Light or SkinAppearance.Dark &&
            draft.Manifest["tokens"] is JsonObject tokens)
        {
            var previousPalette = GetPalette(previousAppearance.Value);
            UpdateBuiltInColor(tokens, "color.surface.window", previousPalette.Window, targetPalette.Window);
            UpdateBuiltInColor(tokens, "color.text.primary", previousPalette.PrimaryText, targetPalette.PrimaryText);
            UpdateBuiltInColor(tokens, "color.accent", previousPalette.Accent, targetPalette.Accent);
            UpdateBuiltInNumber(tokens, "material.fillOpacity", previousPalette.FillOpacity, targetPalette.FillOpacity);
            if (draft.Manifest["background"] is JsonObject background)
            {
                UpdateBuiltInNumber(
                    background,
                    "overlayOpacity",
                    GetDefaultOverlayOpacity(previousAppearance.Value),
                    GetDefaultOverlayOpacity(appearance));
            }
        }

        draft.Manifest["appearance"] = ToManifestAppearance(appearance);
        return draft;
    }

    /// <summary>Creates a detached draft copy, preserving its author, manifest, and resources.</summary>
    public static SkinDraft CreateCopy(SkinPackage package, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var detachedFiles = package.Files.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var detachedPackage = SkinPackageReader.ReadFiles(detachedFiles);
        var draft = SkinDraft.FromPackage(detachedPackage);
        draft.Manifest["id"] = id;
        draft.Manifest["name"] = name;
        _ = draft.BuildPackage();
        return draft;
    }

    /// <summary>Returns the starting image-overlay opacity for the selected appearance.</summary>
    public static double GetDefaultOverlayOpacity(SkinAppearance appearance) => appearance switch
    {
        SkinAppearance.Light => 0.08,
        SkinAppearance.Dark => 0.35,
        _ => throw new ArgumentOutOfRangeException(nameof(appearance), appearance, "只能指定 Light 或 Dark 外观。")
    };

    private static Palette GetPalette(SkinAppearance appearance) => appearance switch
    {
        SkinAppearance.Light => LightPalette,
        SkinAppearance.Dark => DarkPalette,
        _ => throw new ArgumentOutOfRangeException(nameof(appearance), appearance, "只能指定 Light 或 Dark 外观。")
    };

    private static SkinAppearance? ReadAppearance(JsonNode? value) => value is JsonValue jsonValue &&
        jsonValue.TryGetValue<string>(out var text)
            ? text switch
            {
                "light" => SkinAppearance.Light,
                "dark" => SkinAppearance.Dark,
                _ => null
            }
            : null;

    private static string ToManifestAppearance(SkinAppearance appearance) => appearance switch
    {
        SkinAppearance.Light => "light",
        SkinAppearance.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(appearance), appearance, "只能指定 Light 或 Dark 外观。")
    };

    private static void UpdateBuiltInColor(JsonObject tokens, string key, string previous, string next)
    {
        if (tokens[key] is JsonValue value && value.TryGetValue<string>(out var current) &&
            string.Equals(current, previous, StringComparison.OrdinalIgnoreCase))
        {
            tokens[key] = next;
        }
    }

    private static void UpdateBuiltInNumber(JsonObject values, string key, double previous, double next)
    {
        if (values[key] is JsonValue value && value.TryGetValue<double>(out var current) && current == previous)
        {
            values[key] = next;
        }
    }
}
