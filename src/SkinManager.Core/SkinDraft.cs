using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkinManager.Core;

public sealed record SkinCatalogEntry(
    string Id,
    string Name,
    string Version,
    SkinAppearance Appearance,
    string? BackgroundImagePath,
    double? FocusX,
    double? FocusY);

public sealed class SkinDraft
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly Dictionary<string, byte[]> _files;
    private readonly HashSet<string> _generatedFilePaths = new(PathComparer);

    private SkinDraft(JsonObject manifest, Dictionary<string, byte[]> files)
    {
        Manifest = manifest;
        _files = files;
    }

    public JsonObject Manifest { get; private set; }

    public void RestoreManifest(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Manifest = (JsonObject)manifest.DeepClone();
    }

    public static SkinDraft CreateNew(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new SkinDraft(new JsonObject
        {
            ["formatVersion"] = 1,
            ["id"] = id,
            ["name"] = name,
            ["version"] = "1.0.0",
            ["appearance"] = "system"
        }, new Dictionary<string, byte[]>(PathComparer));
    }

    public static SkinDraft FromPackage(SkinPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Files.TryGetValue("skin.json", out var manifestBytes))
        {
            throw new InvalidDataException("皮肤包缺少 skin.json。");
        }

        var manifest = JsonNode.Parse(manifestBytes)?.AsObject()
            ?? throw new InvalidDataException("skin.json 根值必须是 JSON 对象。");
        var files = package.Files
            .Where(pair => !pair.Key.Equals("skin.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
        return new SkinDraft(manifest, files);
    }

    public void SetBackgroundImage(byte[] bytes, string originalName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalName);
        if (bytes.Length == 0 || bytes.Length > SkinPackageReader.MaxSingleFileBytes)
        {
            throw new InvalidDataException("背景图片为空或超过单文件大小上限。");
        }

        var fileName = Path.GetFileName(originalName.Trim());
        if (fileName.Length == 0 || fileName.Any(char.IsControl) ||
            fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':'))
        {
            throw new InvalidDataException("背景图片名称必须是单个包内文件名。");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
        {
            throw new InvalidDataException("背景图片只支持 PNG、JPG 或 JPEG。");
        }

        if (Manifest["background"] is JsonObject currentBackground &&
            currentBackground["image"] is JsonValue currentImage &&
            currentImage.TryGetValue<string>(out var currentPath) &&
            _files.TryGetValue(currentPath, out var currentBytes) && currentBytes.AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var safeStem = new string(stem
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .Take(64)
            .ToArray())
            .Trim('-', '_');
        if (safeStem.Length == 0)
        {
            safeStem = "background";
        }

        var resourcePath = _files
            .Where(pair => Path.GetExtension(pair.Key).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(pair => pair.Value.AsSpan().SequenceEqual(bytes))
            .Key;
        if (resourcePath is null)
        {
            resourcePath = $"assets/{safeStem}-{Guid.NewGuid():N}{extension}";
            _files[resourcePath] = bytes.ToArray();
            _generatedFilePaths.Add(resourcePath);
        }

        var background = Manifest["background"] as JsonObject;
        if (background is null)
        {
            background = new JsonObject();
            Manifest["background"] = background;
        }

        background["image"] = resourcePath;
    }

    public void PruneUnreferencedResources(IEnumerable<JsonObject> retainedManifests)
    {
        ArgumentNullException.ThrowIfNull(retainedManifests);
        var referencedPaths = EnumerateReferencedImages(Manifest);
        foreach (var manifest in retainedManifests)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            referencedPaths.UnionWith(EnumerateReferencedImages(manifest));
        }

        foreach (var path in _generatedFilePaths.ToArray())
        {
            if (referencedPaths.Contains(path))
            {
                continue;
            }

            _generatedFilePaths.Remove(path);
            _files.Remove(path);
        }
    }

    public SkinPackage BuildPackage()
    {
        var referencedPaths = EnumerateReferencedImages(Manifest);
        var files = _files
            .Where(pair => !_generatedFilePaths.Contains(pair.Key) || referencedPaths.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, PathComparer);
        files["skin.json"] = JsonSerializer.SerializeToUtf8Bytes(Manifest);
        return SkinPackageReader.ReadFiles(files);
    }

    private static HashSet<string> EnumerateReferencedImages(JsonObject manifest)
    {
        var result = new HashSet<string>(PathComparer);
        AddImagePath(manifest["background"], result);
        foreach (var sectionName in new[] { "variants", "scenes" })
        {
            if (manifest[sectionName] is not JsonObject section)
            {
                continue;
            }

            foreach (var patch in section.Select(pair => pair.Value))
            {
                if (patch is JsonObject patchObject)
                {
                    AddImagePath(patchObject["background"], result);
                }
            }
        }

        return result;
    }

    private static void AddImagePath(JsonNode? backgroundNode, ISet<string> target)
    {
        if (backgroundNode is JsonObject background &&
            background["image"] is JsonValue imageValue &&
            imageValue.TryGetValue<string>(out var imagePath) &&
            !string.IsNullOrWhiteSpace(imagePath))
        {
            target.Add(imagePath);
        }
    }
}
