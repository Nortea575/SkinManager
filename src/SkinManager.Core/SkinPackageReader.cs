using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkinManager.Core;

public static partial class SkinPackageReader
{
    public const int MaxFiles = 256;
    public const long MaxExpandedBytes = 64L * 1024 * 1024;
    public const long MaxSingleFileBytes = 32L * 1024 * 1024;
    public const long MaxArchiveBytes = 64L * 1024 * 1024;
    private const int MaxSkinJsonBytes = 1024 * 1024;
    private const int MaxZipEntries = MaxFiles * 2;
    private const uint MaxImagePixels = 50_000_000;
    private const uint MaxImageDimension = 16_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    public static SkinPackage ReadZipFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到 ZIP 文件。", path);
        }

        if (info.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("ZIP 文件超过 64 MB，无法导入。 ");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadZipStream(stream);
    }

    public static SkinPackage ReadZipBytes(ReadOnlyMemory<byte> zipBytes)
    {
        if (zipBytes.Length == 0 || zipBytes.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("ZIP 文件为空或超过 64 MB。 ");
        }

        using var stream = new MemoryStream(zipBytes.ToArray(), writable: false);
        return ReadZipStream(stream);
    }

    /// <summary>
    /// Parses an in-memory package using the same limits and validation as ZIP and folder imports.
    /// The file map is copied, but the byte arrays are adopted without copying; callers must not
    /// mutate those arrays after passing them in.
    /// </summary>
    public static SkinPackage ReadFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0 || files.Count > MaxFiles)
        {
            throw new InvalidDataException($"内存皮肤包文件数量必须在 1 到 {MaxFiles} 之间。 ");
        }

        long totalBytes = 0;
        var ownedFiles = new Dictionary<string, byte[]>(PathComparer);
        foreach (var pair in files)
        {
            var path = NormalizeRelativePath(pair.Key, $"$.files.{pair.Key}");
            var bytes = pair.Value ?? throw Invalid($"$.files.{path}", "资源内容不能为空。");
            if (bytes.Length > MaxSingleFileBytes)
            {
                throw Invalid($"$.files.{path}", "超过单文件大小上限。");
            }

            totalBytes = checked(totalBytes + bytes.LongLength);
            if (totalBytes > MaxExpandedBytes)
            {
                throw new InvalidDataException("内存皮肤包总大小超过 64 MB。 ");
            }

            if (!ownedFiles.TryAdd(path, bytes))
            {
                throw Invalid($"$.files.{path}", "存在重复路径（路径大小写不敏感）。");
            }
        }

        return CreatePackage(ownedFiles);
    }

    public static SkinPackage ReadFolder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var selected = Path.GetFullPath(directory);
        if (!Directory.Exists(selected))
        {
            throw new DirectoryNotFoundException("找不到所选皮肤文件夹。 ");
        }

        RejectReparsePoint(selected, "所选文件夹不能是符号链接。 ");
        var root = ResolveFolderRoot(selected);
        return CreatePackage(ReadFolderFiles(root));
    }

    internal static SkinCatalogEntry ReadCatalogEntry(string directory, string folderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        var selected = Path.GetFullPath(directory);
        if (!Directory.Exists(selected))
        {
            throw new DirectoryNotFoundException("找不到皮肤文件夹。 ");
        }

        RejectReparsePoint(selected, "皮肤文件夹不能是符号链接或重解析点。 ");
        var manifestPath = Path.Combine(selected, "skin.json");
        if (!File.Exists(manifestPath))
        {
            throw Invalid("$.skin.json", "皮肤包缺少 skin.json。");
        }

        RejectReparsePoint(manifestPath, "skin.json 不能是符号链接或重解析点。 ");
        var info = new FileInfo(manifestPath);
        if (info.Length == 0 || info.Length > MaxSkinJsonBytes)
        {
            throw Invalid("$.skin.json", "为空或超过 1 MB。");
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(File.ReadAllBytes(manifestPath));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("$.skin.json: 不是有效的 UTF-8 文本。 ", exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"$.skin.json: JSON 格式错误（第 {exception.LineNumber + 1} 行）。", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            RejectDuplicateProperties(root, "$");
            var properties = CheckObject(root, "$", ["formatVersion", "id", "name", "version", "author", "appearance", "tokens", "components", "background", "variants", "scenes"]);
            if (RequiredInt(properties, "formatVersion", "$") != 1)
            {
                throw Invalid("$.formatVersion", "仅支持值 1。");
            }

            var id = RequiredString(properties, "id", "$").Trim();
            if (!SkinIdRegex().IsMatch(id) || !id.Equals(folderId, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("$.id", "皮肤 ID 无效，或与皮肤目录名称不一致。");
            }

            var name = RequiredString(properties, "name", "$").Trim();
            ValidateRequiredText(name, "$.name", 120);
            var version = RequiredString(properties, "version", "$").Trim();
            ValidateRequiredText(version, "$.version", 64);
            var appearanceText = properties.TryGetValue("appearance", out var appearanceElement)
                ? ReadString(appearanceElement, "$.appearance")
                : "system";
            var appearance = appearanceText switch
            {
                "system" => SkinAppearance.System,
                "light" => SkinAppearance.Light,
                "dark" => SkinAppearance.Dark,
                _ => throw Invalid("$.appearance", "只能是 system、light 或 dark。")
            };

            string? imagePath = null;
            double? focusX = null;
            double? focusY = null;
            if (properties.TryGetValue("background", out var backgroundElement))
            {
                var background = ReadBackground(backgroundElement, "$.background");
                imagePath = background.Properties.GetValueOrDefault("image")?.Text;
                focusX = background.Properties.GetValueOrDefault("focusX")?.Number;
                focusY = background.Properties.GetValueOrDefault("focusY")?.Number;
            }

            return new SkinCatalogEntry(id, name, version, appearance, imagePath, focusX, focusY);
        }
    }

    private static SkinPackage ReadZipStream(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxZipEntries)
        {
            throw new InvalidDataException($"ZIP 条目数量无效；最多支持 {MaxFiles} 个文件。 ");
        }

        long declaredTotal = 0;
        var files = new Dictionary<string, byte[]>(PathComparer);
        foreach (var entry in archive.Entries)
        {
            RejectZipLink(entry);
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var trimmed = entry.FullName.TrimEnd('/', '\\');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var path = NormalizeRelativePath(trimmed, $"ZIP.{entry.FullName}");
            if (isDirectory)
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaxSingleFileBytes)
            {
                throw Invalid($"$.files.{path}", "超过单文件大小上限。");
            }

            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > MaxExpandedBytes)
            {
                throw new InvalidDataException("ZIP 解压后总大小超过 64 MB。 ");
            }

            if (files.Count >= MaxFiles)
            {
                throw new InvalidDataException($"ZIP 文件数量超过 {MaxFiles} 个。 ");
            }

            if (files.ContainsKey(path))
            {
                throw Invalid($"$.files.{path}", "ZIP 中存在重复路径（路径大小写不敏感）。");
            }

            using var source = entry.Open();
            using var destination = new MemoryStream((int)entry.Length);
            CopyBounded(source, destination, entry.Length, path);
            if (destination.Length != entry.Length)
            {
                throw Invalid($"$.files.{path}", "ZIP 文件的实际大小与目录不一致。");
            }

            files.Add(path, destination.ToArray());
        }

        return CreatePackage(StripSingleWrapper(files));
    }

    private static Dictionary<string, byte[]> ReadFolderFiles(string root)
    {
        var files = new Dictionary<string, byte[]>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(root);
        long total = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparsePoint(directory, "皮肤文件夹不能包含符号链接或重解析点。 ");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(entry, "皮肤文件夹不能包含符号链接或重解析点。 ");
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var path = NormalizeRelativePath(relative, $"$.files.{relative}");
                var info = new FileInfo(entry);
                if (info.Length > MaxSingleFileBytes)
                {
                    throw Invalid($"$.files.{path}", "超过单文件大小上限。");
                }

                total = checked(total + info.Length);
                if (total > MaxExpandedBytes || files.Count >= MaxFiles)
                {
                    throw new InvalidDataException("皮肤文件夹超过文件数量或总大小上限。 ");
                }

                if (files.ContainsKey(path))
                {
                    throw Invalid($"$.files.{path}", "存在重复路径（路径大小写不敏感）。");
                }

                files.Add(path, File.ReadAllBytes(entry));
            }
        }

        return files;
    }

    private static string ResolveFolderRoot(string selected)
    {
        var rootManifest = Directory.EnumerateFiles(selected)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("skin.json", StringComparison.OrdinalIgnoreCase));
        if (rootManifest is not null)
        {
            return selected;
        }

        var childDirectories = Directory.EnumerateDirectories(selected).ToArray();
        var candidates = childDirectories.Where(path => Directory.EnumerateFiles(path)
            .Any(file => Path.GetFileName(file).Equals("skin.json", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidDataException("所选文件夹或其中唯一一层包装目录必须包含 skin.json。 ");
        }

        if (Directory.EnumerateFiles(selected).Any())
        {
            throw new InvalidDataException("包装目录外不能包含皮肤文件。 ");
        }

        if (childDirectories.Length != 1)
        {
            throw new InvalidDataException("只支持皮肤文件夹或唯一的一层包装目录。 ");
        }

        return candidates[0];
    }

    private static Dictionary<string, byte[]> StripSingleWrapper(Dictionary<string, byte[]> files)
    {
        var manifests = files.Keys.Where(path => path.Equals("skin.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/skin.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("ZIP 根目录或唯一的一层包装目录中必须包含 skin.json。 ");
        }

        var prefix = manifests[0][..^"skin.json".Length];
        if (prefix.Length > 0 && prefix.TrimEnd('/').Contains('/'))
        {
            throw new InvalidDataException("只支持根目录或单层包装目录中的 skin.json。 ");
        }

        if (prefix.Length == 0)
        {
            return files;
        }

        var stripped = new Dictionary<string, byte[]>(PathComparer);
        foreach (var pair in files)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("单层包装目录外还包含文件，无法确定皮肤包根目录。 ");
            }

            var childPath = pair.Key[prefix.Length..];
            if (!stripped.TryAdd(childPath, pair.Value))
            {
                throw Invalid($"$.files.{childPath}", "ZIP 中存在重复路径。");
            }
        }

        return stripped;
    }

    private static SkinPackage CreatePackage(Dictionary<string, byte[]> files)
    {
        if (!files.TryGetValue("skin.json", out var jsonBytes))
        {
            throw Invalid("$.skin.json", "皮肤包缺少 skin.json。");
        }

        if (jsonBytes.Length == 0 || jsonBytes.Length > MaxSkinJsonBytes)
        {
            throw Invalid("$.skin.json", "为空或超过 1 MB。");
        }

        foreach (var pair in files)
        {
            if (pair.Key.Equals("skin.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSupportedImage(pair.Key))
            {
                throw Invalid($"$.files.{pair.Key}", "仅支持 skin.json 与 PNG、JPG、JPEG、GIF 图片资源；不支持 CSS 或脚本文件。");
            }

            ValidateImage(pair.Key, pair.Value, $"$.files.{pair.Key}");
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(jsonBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("$.skin.json: 不是有效的 UTF-8 文本。 ", exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"$.skin.json: JSON 格式错误（第 {exception.LineNumber + 1} 行）。", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            RejectDuplicateProperties(root, "$");
            var rootProperties = CheckObject(root, "$", ["formatVersion", "id", "name", "version", "author", "appearance", "tokens", "components", "background", "variants", "scenes"]);
            var formatVersion = RequiredInt(rootProperties, "formatVersion", "$");
            if (formatVersion != 1)
            {
                throw Invalid("$.formatVersion", "仅支持值 1。");
            }

            var id = RequiredString(rootProperties, "id", "$").Trim();
            if (!SkinIdRegex().IsMatch(id))
            {
                throw Invalid("$.id", "只能由字母、数字、点、下划线和连字符组成，且必须以字母或数字开头。");
            }

            var name = RequiredString(rootProperties, "name", "$").Trim();
            ValidateRequiredText(name, "$.name", 120);
            var version = RequiredString(rootProperties, "version", "$").Trim();
            ValidateRequiredText(version, "$.version", 64);
            var author = OptionalString(rootProperties, "author", "$");
            if (author is not null)
            {
                ValidateRequiredText(author, "$.author", 120);
            }

            var appearanceText = rootProperties.TryGetValue("appearance", out var appearanceElement)
                ? ReadString(appearanceElement, "$.appearance")
                : "system";
            var appearance = appearanceText switch
            {
                "system" => SkinAppearance.System,
                "light" => SkinAppearance.Light,
                "dark" => SkinAppearance.Dark,
                _ => throw Invalid("$.appearance", "只能是 system、light 或 dark。")
            };

            var tokens = rootProperties.TryGetValue("tokens", out var tokensElement)
                ? ReadTokens(tokensElement, "$.tokens")
                : EmptyTokens();
            var components = rootProperties.TryGetValue("components", out var componentsElement)
                ? ReadComponents(componentsElement, "$.components")
                : EmptyComponents();
            var background = rootProperties.TryGetValue("background", out var backgroundElement)
                ? ReadBackground(backgroundElement, "$.background")
                : null;
            var variants = rootProperties.TryGetValue("variants", out var variantsElement)
                ? ReadVariants(variantsElement)
                : new Dictionary<SkinAppearance, SkinPatch>();
            var scenes = rootProperties.TryGetValue("scenes", out var scenesElement)
                ? ReadScenes(scenesElement)
                : new Dictionary<SkinScene, SkinPatch>();

            ValidateBackgroundFiles(
                files,
                background,
                "$.background",
                variants.Select(pair => (pair.Value, $"$.variants.{pair.Key.ToString().ToLowerInvariant()}.background"))
                    .Concat(scenes.Select(pair => (pair.Value, $"$.scenes.{pair.Key.ToString().ToLowerInvariant()}.background"))));
            var definition = new SkinDefinition(formatVersion, author, tokens, components, background, variants, scenes);
            var package = new SkinPackage(id, name, version, appearance, definition, files);
            foreach (var mode in new[] { SkinAppearance.Light, SkinAppearance.Dark })
            {
                foreach (var scene in new[] { SkinScene.Home, SkinScene.Conversation })
                {
                    _ = SkinResolver.Resolve(package, mode, scene);
                }
            }

            return package;
        }
    }

    private static Dictionary<string, SkinValue> ReadTokens(JsonElement element, string path)
    {
        var properties = CheckObject(element, path, SkinDefinitionCatalog.Tokens.Select(item => item.Key));
        var result = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            SkinDefinitionCatalog.TryGetToken(pair.Key, out var definition);
            result.Add(pair.Key, ReadTypedValue(pair.Value, pair.Key, definition.Kind, $"{path}.{pair.Key}", definition.AllowNoneText));
        }

        return result;
    }

    private static Dictionary<string, ComponentStyle> ReadComponents(JsonElement element, string path)
    {
        var properties = CheckObject(element, path, SkinDefinitionCatalog.Components.Select(item => item.Id));
        var result = new Dictionary<string, ComponentStyle>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            var componentPath = $"{path}.{pair.Key}";
            var style = CheckObject(pair.Value, componentPath, SkinDefinitionCatalog.ComponentProperties.Select(item => item.Key).Append("states"));
            var values = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
            foreach (var property in style.Where(item => item.Key != "states"))
            {
                SkinDefinitionCatalog.TryGetComponentProperty(property.Key, out var definition);
                values.Add(property.Key, ReadTypedValue(property.Value, property.Key, definition.Kind, $"{componentPath}.{property.Key}", definition.AllowNoneText));
            }

            var states = style.TryGetValue("states", out var statesElement)
                ? ReadStates(statesElement, $"{componentPath}.states")
                : new Dictionary<string, IReadOnlyDictionary<string, SkinValue>>(StringComparer.Ordinal);
            result.Add(pair.Key, new ComponentStyle(values, states));
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, SkinValue>> ReadStates(JsonElement element, string path)
    {
        var states = CheckObject(element, path, SkinDefinitionCatalog.States.Select(item => item.Id));
        var result = new Dictionary<string, IReadOnlyDictionary<string, SkinValue>>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var statePath = $"{path}.{state.Key}";
            var properties = CheckObject(state.Value, statePath, SkinDefinitionCatalog.ComponentProperties.Select(item => item.Key));
            var values = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                SkinDefinitionCatalog.TryGetComponentProperty(property.Key, out var definition);
                values.Add(property.Key, ReadTypedValue(property.Value, property.Key, definition.Kind, $"{statePath}.{property.Key}", definition.AllowNoneText));
            }

            result.Add(state.Key, values);
        }

        return result;
    }

    private static BackgroundDefinition ReadBackground(JsonElement element, string path)
    {
        var properties = CheckObject(element, path, SkinDefinitionCatalog.BackgroundProperties.Select(item => item.Key));
        var result = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
        foreach (var pair in properties)
        {
            SkinDefinitionCatalog.TryGetBackgroundProperty(pair.Key, out var definition);

            SkinValue value;
            if (definition.Kind == SkinValueKind.String)
            {
                var text = ReadString(pair.Value, $"{path}.{pair.Key}");
                if (pair.Key == "image")
                {
                    text = NormalizeRelativePath(text, $"{path}.image");
                    if (!IsSupportedImage(text))
                    {
                        throw Invalid($"{path}.image", "背景图片必须是 PNG、JPG、JPEG 或 GIF 文件。");
                    }
                }
                else if (text is not ("cover" or "contain"))
                {
                    throw Invalid($"{path}.fit", "只能是 cover 或 contain。");
                }

                value = SkinValue.TextValue(definition.Kind, text);
            }
            else
            {
                value = ReadTypedValue(pair.Value, pair.Key, definition.Kind, $"{path}.{pair.Key}", definition.AllowNoneText);
            }

            result.Add(pair.Key, value);
        }

        return new BackgroundDefinition(result);
    }

    private static Dictionary<SkinAppearance, SkinPatch> ReadVariants(JsonElement element)
    {
        const string path = "$.variants";
        var properties = CheckObject(element, path, ["light", "dark"]);
        var result = new Dictionary<SkinAppearance, SkinPatch>();
        foreach (var pair in properties)
        {
            var mode = pair.Key == "light" ? SkinAppearance.Light : SkinAppearance.Dark;
            result.Add(mode, ReadPatch(pair.Value, $"{path}.{pair.Key}"));
        }

        return result;
    }

    private static Dictionary<SkinScene, SkinPatch> ReadScenes(JsonElement element)
    {
        const string path = "$.scenes";
        var properties = CheckObject(element, path, ["home", "conversation"]);
        var result = new Dictionary<SkinScene, SkinPatch>();
        foreach (var pair in properties)
        {
            var scene = pair.Key == "home" ? SkinScene.Home : SkinScene.Conversation;
            result.Add(scene, ReadPatch(pair.Value, $"{path}.{pair.Key}"));
        }

        return result;
    }

    private static SkinPatch ReadPatch(JsonElement element, string path)
    {
        var properties = CheckObject(element, path, ["tokens", "components", "background"]);
        var tokens = properties.TryGetValue("tokens", out var tokenElement) ? ReadTokens(tokenElement, $"{path}.tokens") : EmptyTokens();
        var components = properties.TryGetValue("components", out var componentElement) ? ReadComponents(componentElement, $"{path}.components") : EmptyComponents();
        var background = properties.TryGetValue("background", out var backgroundElement) ? ReadBackground(backgroundElement, $"{path}.background") : null;
        return new SkinPatch(tokens, components, background);
    }

    private static SkinValue ReadTypedValue(JsonElement element, string target, SkinValueKind expected, string path, bool allowNone)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            throw Invalid(path, "不允许 null；省略属性表示沿用原值。");
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (allowNone && text == "none")
            {
                return SkinValue.TextValue(expected, "none");
            }

            var reference = TokenReferenceRegex().Match(text);
            if (reference.Success)
            {
                return SkinValue.Reference(reference.Groups["token"].Value);
            }

            if (text.Contains('{') || text.Contains('}'))
            {
                throw Invalid(path, "令牌引用必须是完整的 {token.name} 形式。");
            }

            if (expected == SkinValueKind.Length)
            {
                var pxMatch = PxLengthRegex().Match(text.Trim());
                if (pxMatch.Success && double.TryParse(pxMatch.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pxValue) && double.IsFinite(pxValue))
                {
                    return CreateNumberValue(expected, pxValue, path);
                }

                throw Invalid(path, "px 长度必须是数字或带 px 单位的字符串。");
            }

            if (expected == SkinValueKind.Color)
            {
                if (!CssColorParser.TryParse(text, out _))
                {
                    throw Invalid(path, "必须是有效 CSS 颜色。");
                }

                return SkinValue.TextValue(expected, text);
            }

            if (expected == SkinValueKind.FontFamily)
            {
                var family = text.Trim();
                if (family.Length is 0 or > 128 || family.Any(char.IsControl))
                {
                    throw Invalid(path, "必须是有效系统字体名称。");
                }

                return SkinValue.TextValue(expected, family);
            }

            if (expected == SkinValueKind.Shadow)
            {
                throw Invalid(path, "shadow 必须是完整 shadow 对象、none 或同类型令牌引用。");
            }

            throw Invalid(path, $"应为 {DescribeKind(expected)}，不接受字符串。");
        }

        if (element.ValueKind == JsonValueKind.Number && expected is SkinValueKind.Length or SkinValueKind.Number or SkinValueKind.Ratio)
        {
            if (!element.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                throw Invalid(path, "必须是有限数字。");
            }

            return CreateNumberValue(expected, number, path);
        }

        if (element.ValueKind == JsonValueKind.Object && expected == SkinValueKind.Shadow)
        {
            return SkinValue.ShadowValue(ReadShadow(element, path));
        }

        throw Invalid(path, $"应为 {DescribeKind(expected)}。");
    }

    private static SkinValue CreateNumberValue(SkinValueKind kind, double value, string path)
    {
        if (!double.IsFinite(value))
        {
            throw Invalid(path, "必须是有限数字。");
        }

        switch (kind)
        {
            case SkinValueKind.Length when value is < 0 or > 4096:
                throw Invalid(path, "px 长度必须在 0 到 4096 之间。");
            case SkinValueKind.Ratio when value is < 0 or > 1:
                throw Invalid(path, "透明度或位置比例必须在 0 到 1 之间。");
            case SkinValueKind.Number when value is < 0 or > 1000:
                throw Invalid(path, "数字必须在 0 到 1000 之间。");
        }

        return SkinValue.NumberValue(kind, value);
    }

    private static ShadowStyle ReadShadow(JsonElement element, string path)
    {
        var properties = CheckObject(element, path, ["x", "y", "blur", "spread", "color", "opacity"]);
        foreach (var required in new[] { "x", "y", "blur", "spread", "color", "opacity" })
        {
            if (!properties.ContainsKey(required))
            {
                throw Invalid($"{path}.{required}", "shadow 属性为必填项。");
            }
        }

        var x = ReadFiniteNumber(properties["x"], $"{path}.x", -4096, 4096);
        var y = ReadFiniteNumber(properties["y"], $"{path}.y", -4096, 4096);
        var blur = ReadFiniteNumber(properties["blur"], $"{path}.blur", 0, 4096);
        var spread = ReadFiniteNumber(properties["spread"], $"{path}.spread", -4096, 4096);
        var color = ReadString(properties["color"], $"{path}.color");
        if (!CssColorParser.TryParse(color, out _))
        {
            throw Invalid($"{path}.color", "必须是有效 CSS 颜色。");
        }

        var opacity = ReadFiniteNumber(properties["opacity"], $"{path}.opacity", 0, 1);
        return new ShadowStyle(x, y, blur, spread, color, opacity);
    }

    private static Dictionary<string, JsonElement> CheckObject(JsonElement element, string path, IEnumerable<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(path, "必须是对象。");
        }

        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var propertyPath = JoinPath(path, property.Name);
            if (!allowedSet.Contains(property.Name))
            {
                throw Invalid(propertyPath, "未知字段。");
            }

            if (!result.TryAdd(property.Name, property.Value))
            {
                throw Invalid(propertyPath, "JSON 字段重复。");
            }
        }

        return result;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = JoinPath(path, property.Name);
                if (!names.Add(property.Name))
                {
                    throw Invalid(propertyPath, "JSON 字段重复。");
                }

                RejectDuplicateProperties(property.Value, propertyPath);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> properties, string name, string path)
    {
        if (!properties.TryGetValue(name, out var value))
        {
            throw Invalid($"{path}.{name}", "为必填字段。");
        }

        return ReadString(value, $"{path}.{name}");
    }

    private static string? OptionalString(IReadOnlyDictionary<string, JsonElement> properties, string name, string path) =>
        properties.TryGetValue(name, out var value) ? ReadString(value, $"{path}.{name}") : null;

    private static string ReadString(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(path, "必须是字符串，且不允许 null。");
        }

        return value.GetString()!;
    }

    private static int RequiredInt(IReadOnlyDictionary<string, JsonElement> properties, string name, string path)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid($"{path}.{name}", "必须是整数。");
        }

        return number;
    }

    private static double ReadFiniteNumber(JsonElement element, string path, double minimum, double maximum)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || !double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw Invalid(path, $"必须是 {minimum} 到 {maximum} 之间的有限数字。");
        }

        return value;
    }

    private static void ValidateRequiredText(string value, string path, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw Invalid(path, $"不能为空、不能含控制字符且最多 {maximum} 个字符。");
        }
    }

    private static void ValidateBackgroundFiles(
        IReadOnlyDictionary<string, byte[]> files,
        BackgroundDefinition? background,
        string basePath,
        IEnumerable<(SkinPatch Patch, string Path)> patches)
    {
        ValidateBackgroundFile(files, background, basePath);
        foreach (var (patch, path) in patches)
        {
            ValidateBackgroundFile(files, patch.Background, path);
        }
    }

    private static void ValidateBackgroundFile(IReadOnlyDictionary<string, byte[]> files, BackgroundDefinition? background, string path)
    {
        if (background?.Properties.TryGetValue("image", out var image) == true && image.Text is not null && !files.ContainsKey(image.Text))
        {
            throw Invalid($"{path}.image", $"皮肤包缺少背景图片“{image.Text}”。");
        }
    }

    private static bool IsSupportedImage(string path)
    {
        return IsSupportedImagePath(path);
    }

    public static bool IsSupportedImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetImageMediaType(string imagePath) => Path.GetExtension(imagePath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => throw new ArgumentException("图片资源必须是 PNG、JPG、JPEG 或 GIF 文件。", nameof(imagePath))
    };

    public static GifImageInfo ReadGifInfo(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 ||
            !(bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            throw new InvalidDataException("不是有效的 GIF87a 或 GIF89a 文件头。 ");
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        if (width == 0 || height == 0 || width > MaxImageDimension || height > MaxImageDimension ||
            (uint)width * height > MaxImagePixels)
        {
            throw new InvalidDataException("GIF 逻辑画布尺寸无效或过大。 ");
        }

        var screenPacked = bytes[10];
        var hasGlobalColorTable = (screenPacked & 0x80) != 0;
        var backgroundColorIndex = bytes[11];
        byte backgroundRed = 0;
        byte backgroundGreen = 0;
        byte backgroundBlue = 0;
        var globalColorTableEntries = 0;
        var offset = 13;
        if (hasGlobalColorTable)
        {
            globalColorTableEntries = 1 << ((screenPacked & 0x07) + 1);
            if (backgroundColorIndex >= globalColorTableEntries)
            {
                throw new InvalidDataException("GIF 逻辑画布背景颜色索引无效。 ");
            }

            var tableLength = 3 * globalColorTableEntries;
            var tableStart = offset;
            SkipGifBytes(bytes, ref offset, tableLength, "GIF 全局颜色表不完整。");
            var backgroundOffset = tableStart + 3 * backgroundColorIndex;
            backgroundRed = bytes[backgroundOffset];
            backgroundGreen = bytes[backgroundOffset + 1];
            backgroundBlue = bytes[backgroundOffset + 2];
        }

        var frameCount = 0;
        var firstFrameHasTransparency = false;
        byte firstFrameTransparentIndex = 0;
        var currentFrameHasTransparency = false;
        byte currentFrameTransparentIndex = 0;
        GifImageInfo firstFrame = default;
        while (offset < bytes.Length)
        {
            var introducer = bytes[offset++];
            if (introducer == 0x3b)
            {
                if (offset != bytes.Length || frameCount == 0)
                {
                    throw new InvalidDataException("GIF 结束标记位置无效，或文件不包含图像帧。 ");
                }

                return firstFrame;
            }

            if (introducer == 0x21)
            {
                if (offset >= bytes.Length)
                {
                    throw new InvalidDataException("GIF 扩展块不完整。 ");
                }

                var label = bytes[offset++];
                if (label == 0xf9)
                {
                    if (offset + 6 > bytes.Length || bytes[offset++] != 4)
                    {
                        throw new InvalidDataException("GIF 图形控制扩展块不完整。 ");
                    }

                    currentFrameHasTransparency = (bytes[offset] & 0x01) != 0;
                    currentFrameTransparentIndex = bytes[offset + 3];
                    offset += 4;
                    if (bytes[offset++] != 0)
                    {
                        throw new InvalidDataException("GIF 图形控制扩展块缺少结束字节。 ");
                    }

                    continue;
                }

                var firstBlockLength = label switch
                {
                    0x01 => 12,
                    0xff => 11,
                    _ => 0
                };
                _ = SkipGifSubBlocks(bytes, ref offset, firstBlockLength, "GIF 扩展数据不完整。");
                continue;
            }

            if (introducer != 0x2c)
            {
                throw new InvalidDataException("GIF 数据块标记无效。 ");
            }

            if (offset + 9 > bytes.Length)
            {
                throw new InvalidDataException("GIF 图像描述符不完整。 ");
            }

            var left = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..(offset + 2)]);
            var top = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..(offset + 4)]);
            var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 4)..(offset + 6)]);
            var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 6)..(offset + 8)]);
            var framePacked = bytes[offset + 8];
            offset += 9;
            if (frameWidth == 0 || frameHeight == 0 || (uint)left + frameWidth > width || (uint)top + frameHeight > height)
            {
                throw new InvalidDataException("GIF 图像帧超出逻辑画布范围。 ");
            }

            var hasLocalColorTable = (framePacked & 0x80) != 0;
            if (!hasGlobalColorTable && !hasLocalColorTable)
            {
                throw new InvalidDataException("GIF 图像帧缺少全局或局部颜色表。 ");
            }

            var localColorTableEntries = 0;
            if (hasLocalColorTable)
            {
                localColorTableEntries = 1 << ((framePacked & 0x07) + 1);
                var tableLength = 3 * localColorTableEntries;
                SkipGifBytes(bytes, ref offset, tableLength, "GIF 局部颜色表不完整。");
            }

            var frameColorTableEntries = hasLocalColorTable ? localColorTableEntries : globalColorTableEntries;
            if (currentFrameHasTransparency && currentFrameTransparentIndex >= frameColorTableEntries)
            {
                throw new InvalidDataException("GIF 图形控制扩展的透明颜色索引无效。 ");
            }

            if (offset >= bytes.Length || bytes[offset++] is < 2 or > 8)
            {
                throw new InvalidDataException("GIF LZW 数据码宽度无效。 ");
            }

            var dataBytes = SkipGifSubBlocks(bytes, ref offset, 0, "GIF 图像数据不完整。");
            if (dataBytes == 0)
            {
                throw new InvalidDataException("GIF 图像帧缺少压缩数据。 ");
            }

            if (frameCount++ == 0)
            {
                firstFrameHasTransparency = currentFrameHasTransparency;
                firstFrameTransparentIndex = currentFrameTransparentIndex;
                firstFrame = new GifImageInfo(
                    width,
                    height,
                    left,
                    top,
                    frameWidth,
                    frameHeight,
                    hasGlobalColorTable,
                    backgroundColorIndex,
                    backgroundRed,
                    backgroundGreen,
                    backgroundBlue,
                    firstFrameHasTransparency,
                    firstFrameTransparentIndex);
            }

            currentFrameHasTransparency = false;
            currentFrameTransparentIndex = 0;
        }

        throw new InvalidDataException("GIF 文件缺少结束标记。 ");
    }

    private static void SkipGifBytes(ReadOnlySpan<byte> bytes, ref int offset, int count, string error)
    {
        if (count < 0 || offset > bytes.Length - count)
        {
            throw new InvalidDataException(error);
        }

        offset += count;
    }

    private static int SkipGifSubBlocks(ReadOnlySpan<byte> bytes, ref int offset, int firstBlockLength, string error)
    {
        var totalLength = 0;
        var isFirstBlock = true;
        while (true)
        {
            if (offset >= bytes.Length)
            {
                throw new InvalidDataException(error);
            }

            var length = bytes[offset++];
            if (length == 0)
            {
                if (isFirstBlock && firstBlockLength > 0)
                {
                    throw new InvalidDataException(error);
                }

                return totalLength;
            }

            if (isFirstBlock && firstBlockLength > 0 && length != firstBlockLength)
            {
                throw new InvalidDataException(error);
            }

            SkipGifBytes(bytes, ref offset, length, error);
            totalLength += length;
            isFirstBlock = false;
        }
    }

    internal static void ValidateImage(string path, byte[] bytes, string errorPath)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                ValidatePng(bytes);
                return;
            }

            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                ValidateJpeg(bytes);
                return;
            }

            if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                _ = ReadGifInfo(bytes);
                return;
            }

            throw Invalid(errorPath, "背景图片必须是 PNG、JPG、JPEG 或 GIF 文件。");
        }
        catch (InvalidDataException exception) when (!exception.Message.StartsWith(errorPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{errorPath}: {exception.Message}", exception);
        }
    }

    private static void ValidatePng(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("不是完整 PNG 或缺少有效 IHDR。");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width == 0 || height == 0 || (ulong)width * height > 50_000_000UL || width > 16_000 || height > 16_000)
        {
            throw new InvalidDataException("PNG 尺寸无效或过大。");
        }

        var hasData = false;
        var hasEnd = false;
        var offset = 8;
        while (offset + 12 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var chunkSize = (ulong)length + 12;
            if (chunkSize > (ulong)(bytes.Length - offset))
            {
                throw new InvalidDataException("PNG 内容不完整。");
            }

            var type = bytes.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8) && length > 0)
            {
                hasData = true;
            }

            if (type.SequenceEqual("IEND"u8))
            {
                hasEnd = length == 0 && offset + 12 == bytes.Length;
                break;
            }

            offset += (int)chunkSize;
        }

        if (!hasData || !hasEnd)
        {
            throw new InvalidDataException("PNG 缺少图像数据或结束标记。");
        }
    }

    private static void ValidateJpeg(byte[] bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
        {
            throw new InvalidDataException("JPEG 缺少起止标记。");
        }

        var hasFrame = false;
        var hasScan = false;
        var width = 0;
        var height = 0;
        var index = 2;
        while (index + 3 < bytes.Length)
        {
            if (bytes[index] != 0xff)
            {
                throw new InvalidDataException("JPEG 标记结构无效。");
            }

            while (index < bytes.Length && bytes[index] == 0xff)
            {
                index++;
            }

            if (index >= bytes.Length)
            {
                break;
            }

            var marker = bytes[index++];
            if (marker == 0xda)
            {
                hasScan = true;
                break;
            }

            if (marker is 0xd8 or 0xd9 or >= 0xd0 and <= 0xd7 or 0x01)
            {
                continue;
            }

            if (index + 2 > bytes.Length)
            {
                throw new InvalidDataException("JPEG 段长度无效。");
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index, 2));
            if (segmentLength < 2 || index + segmentLength > bytes.Length)
            {
                throw new InvalidDataException("JPEG 段内容不完整。");
            }

            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (segmentLength < 8)
                {
                    throw new InvalidDataException("JPEG 尺寸段无效。");
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index + 5, 2));
                hasFrame = true;
            }

            index += segmentLength;
        }

        if (!hasFrame || !hasScan || width == 0 || height == 0 || (long)width * height > 50_000_000L)
        {
            throw new InvalidDataException("JPEG 缺少有效图像尺寸或扫描数据。");
        }
    }

    internal static string NormalizeRelativePath(string path, string errorPath)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || Path.IsPathRooted(path))
        {
            throw Invalid(errorPath, "必须是包内相对路径。");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)))
        {
            throw Invalid(errorPath, "路径包含不允许的片段。");
        }

        return string.Join('/', segments);
    }

    private static void CopyBounded(Stream source, Stream destination, long expectedLength, string path)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            copied = checked(copied + read);
            if (copied > expectedLength || copied > MaxSingleFileBytes)
            {
                throw Invalid($"$.files.{path}", "实际解压大小超过声明值或单文件上限。");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static void RejectZipLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = entry.ExternalAttributes & 0xFFFF;
        if (unixType == 0xA000 || (dosAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid($"$.files.{entry.FullName}", "ZIP 条目是符号链接或重解析点。");
        }
    }

    private static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(message);
        }
    }

    private static Dictionary<string, SkinValue> EmptyTokens() => new(StringComparer.Ordinal);
    private static Dictionary<string, ComponentStyle> EmptyComponents() => new(StringComparer.Ordinal);

    private static InvalidDataException Invalid(string path, string message) => new($"{path}: {message}");

    private static string DescribeKind(SkinValueKind kind) => kind switch
    {
        SkinValueKind.Color => "CSS 颜色",
        SkinValueKind.Length => "px 长度数字",
        SkinValueKind.Number => "数字",
        SkinValueKind.Ratio => "0 到 1 之间的数字",
        SkinValueKind.FontFamily => "系统字体名称",
        SkinValueKind.Shadow => "shadow 对象",
        _ => "有效值"
    };

    private static string JoinPath(string path, string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? $"{path}.{name}"
            : $"{path}[{JsonSerializer.Serialize(name)}]";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SkinIdRegex();

    [GeneratedRegex("^\\{(?<token>[A-Za-z][A-Za-z0-9]*(?:\\.[A-Za-z][A-Za-z0-9]*)+)\\}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenReferenceRegex();

    [GeneratedRegex("^(?<number>(?:\\d+(?:\\.\\d*)?|\\.\\d+))px$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PxLengthRegex();
}
