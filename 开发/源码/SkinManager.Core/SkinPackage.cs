using System.Collections.ObjectModel;

namespace SkinManager.Core;

public sealed class SkinPackage
{
    internal SkinPackage(
        string id,
        string name,
        string version,
        SkinAppearance appearance,
        SkinDefinition definition,
        IReadOnlyDictionary<string, byte[]> files)
    {
        Id = id;
        Name = name;
        Version = version;
        Appearance = appearance;
        Definition = definition;

        // Package readers transfer ownership of their buffers to this package. Keep a
        // private dictionary snapshot while sharing the byte arrays with resolved views.
        Files = new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase));
        ImagePath = ReadBaseString("image");
        ImageBytes = ImagePath is not null && Files.TryGetValue(ImagePath, out var bytes) ? bytes : null;
        FocusX = ReadBaseNumber("focusX");
        FocusY = ReadBaseNumber("focusY");
        Colors = SkinResolver.Resolve(this, appearance == SkinAppearance.Dark ? SkinAppearance.Dark : SkinAppearance.Light, SkinScene.Home).Colors;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public SkinAppearance Appearance { get; }
    public SkinDefinition Definition { get; }
    public IReadOnlyDictionary<string, byte[]> Files { get; }
    public string? ImagePath { get; }
    public byte[]? ImageBytes { get; }
    public double? FocusX { get; }
    public double? FocusY { get; }
    public IReadOnlyDictionary<string, string> Colors { get; }

    public bool TryGetColor(string key, out string value) => Colors.TryGetValue(key, out value!);

    private string? ReadBaseString(string property)
    {
        if (Definition.Background?.Properties.TryGetValue(property, out var value) == true && value.Text is not null)
        {
            return value.Text;
        }

        return null;
    }

    private double? ReadBaseNumber(string property) =>
        Definition.Background?.Properties.TryGetValue(property, out var value) == true && value.Number.HasValue
            ? value.Number
            : null;
}

public sealed class DuplicateSkinException(string id)
    : InvalidOperationException($"皮肤 ID“{id}”已存在。")
{
    public string SkinId { get; } = id;
}
