using System.Collections.ObjectModel;

namespace SkinManager.Core;

public enum SkinAppearance
{
    System,
    Light,
    Dark
}

public enum SkinScene
{
    Home,
    Conversation
}

public enum SkinValueKind
{
    Color,
    Length,
    Number,
    Ratio,
    FontFamily,
    String,
    Shadow,
    TokenReference
}

public sealed record ShadowStyle(double X, double Y, double Blur, double Spread, string Color, double Opacity);

public sealed class SkinValue
{
    private SkinValue(SkinValueKind kind, string? text, double? number, ShadowStyle? shadow)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Shadow = shadow;
    }

    public SkinValueKind Kind { get; }
    public string? Text { get; }
    public double? Number { get; }
    public ShadowStyle? Shadow { get; }

    internal static SkinValue TextValue(SkinValueKind kind, string value) => new(kind, value, null, null);
    internal static SkinValue NumberValue(SkinValueKind kind, double value) => new(kind, null, value, null);
    internal static SkinValue ShadowValue(ShadowStyle value) => new(SkinValueKind.Shadow, null, null, value);
    internal static SkinValue Reference(string token) => new(SkinValueKind.TokenReference, token, null, null);
}

public sealed class ComponentStyle
{
    internal ComponentStyle(
        IReadOnlyDictionary<string, SkinValue> properties,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SkinValue>> states)
    {
        Properties = Freeze(properties);
        States = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, SkinValue>>(
            states.ToDictionary(pair => pair.Key, pair => Freeze(pair.Value), StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, SkinValue> Properties { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SkinValue>> States { get; }

    private static IReadOnlyDictionary<string, SkinValue> Freeze(IReadOnlyDictionary<string, SkinValue> source) =>
        new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(source, StringComparer.Ordinal));
}

public sealed class BackgroundDefinition
{
    internal BackgroundDefinition(IReadOnlyDictionary<string, SkinValue> properties) =>
        Properties = new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(properties, StringComparer.Ordinal));

    public IReadOnlyDictionary<string, SkinValue> Properties { get; }
}

public sealed class SkinPatch
{
    internal SkinPatch(
        IReadOnlyDictionary<string, SkinValue> tokens,
        IReadOnlyDictionary<string, ComponentStyle> components,
        BackgroundDefinition? background)
    {
        Tokens = new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(tokens, StringComparer.Ordinal));
        Components = new ReadOnlyDictionary<string, ComponentStyle>(new Dictionary<string, ComponentStyle>(components, StringComparer.Ordinal));
        Background = background;
    }

    public IReadOnlyDictionary<string, SkinValue> Tokens { get; }
    public IReadOnlyDictionary<string, ComponentStyle> Components { get; }
    public BackgroundDefinition? Background { get; }
}

public sealed class SkinDefinition
{
    internal SkinDefinition(
        int formatVersion,
        string? author,
        IReadOnlyDictionary<string, SkinValue> tokens,
        IReadOnlyDictionary<string, ComponentStyle> components,
        BackgroundDefinition? background,
        IReadOnlyDictionary<SkinAppearance, SkinPatch> variants,
        IReadOnlyDictionary<SkinScene, SkinPatch> scenes)
    {
        FormatVersion = formatVersion;
        Author = author;
        Tokens = new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(tokens, StringComparer.Ordinal));
        Components = new ReadOnlyDictionary<string, ComponentStyle>(new Dictionary<string, ComponentStyle>(components, StringComparer.Ordinal));
        Background = background;
        Variants = new ReadOnlyDictionary<SkinAppearance, SkinPatch>(new Dictionary<SkinAppearance, SkinPatch>(variants));
        Scenes = new ReadOnlyDictionary<SkinScene, SkinPatch>(new Dictionary<SkinScene, SkinPatch>(scenes));
    }

    public int FormatVersion { get; }
    public string? Author { get; }
    public IReadOnlyDictionary<string, SkinValue> Tokens { get; }
    public IReadOnlyDictionary<string, ComponentStyle> Components { get; }
    public BackgroundDefinition? Background { get; }
    public IReadOnlyDictionary<SkinAppearance, SkinPatch> Variants { get; }
    public IReadOnlyDictionary<SkinScene, SkinPatch> Scenes { get; }
}

public sealed class ResolvedComponentStyle
{
    internal ResolvedComponentStyle(
        IReadOnlyDictionary<string, SkinValue> properties,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SkinValue>> states)
    {
        Properties = new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(properties, StringComparer.Ordinal));
        States = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, SkinValue>>(
            states.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, SkinValue>)new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(pair.Value, StringComparer.Ordinal)),
                StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, SkinValue> Properties { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, SkinValue>> States { get; }
}

public sealed class ResolvedBackground
{
    internal ResolvedBackground(
        string? color,
        string? imagePath,
        byte[]? imageBytes,
        string? fit,
        double? focusX,
        double? focusY,
        double? opacity,
        string? overlayColor,
        double? overlayOpacity)
    {
        Color = color;
        ImagePath = imagePath;
        ImageBytes = imageBytes;
        Fit = fit;
        FocusX = focusX;
        FocusY = focusY;
        Opacity = opacity;
        OverlayColor = overlayColor;
        OverlayOpacity = overlayOpacity;
    }

    public string? Color { get; }
    public string? ImagePath { get; }
    public byte[]? ImageBytes { get; }
    public string? Fit { get; }
    public double? FocusX { get; }
    public double? FocusY { get; }
    public double? Opacity { get; }
    public string? OverlayColor { get; }
    public double? OverlayOpacity { get; }
}

public sealed class ResolvedSkin
{
    internal ResolvedSkin(
        SkinAppearance appearance,
        SkinScene scene,
        double? panelOpacityOverride,
        IReadOnlyDictionary<string, SkinValue> tokens,
        IReadOnlyDictionary<string, ResolvedComponentStyle> components,
        ResolvedBackground? background)
    {
        Appearance = appearance;
        Scene = scene;
        PanelOpacityOverride = panelOpacityOverride;
        Tokens = new ReadOnlyDictionary<string, SkinValue>(new Dictionary<string, SkinValue>(tokens, StringComparer.Ordinal));
        Components = new ReadOnlyDictionary<string, ResolvedComponentStyle>(new Dictionary<string, ResolvedComponentStyle>(components, StringComparer.Ordinal));
        Colors = new ReadOnlyDictionary<string, string>(tokens
            .Where(pair => pair.Value.Kind == SkinValueKind.Color && pair.Value.Text is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text!, StringComparer.Ordinal));
        Background = background;
    }

    public SkinAppearance Appearance { get; }
    public SkinScene Scene { get; }
    public double? PanelOpacityOverride { get; }
    public IReadOnlyDictionary<string, SkinValue> Tokens { get; }
    public IReadOnlyDictionary<string, ResolvedComponentStyle> Components { get; }
    public IReadOnlyDictionary<string, string> Colors { get; }
    public ResolvedBackground? Background { get; }
}
