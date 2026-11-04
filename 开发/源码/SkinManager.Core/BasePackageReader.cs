using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SkinManager.Core;

public static class BasePackageReader
{
    public const int MaxFiles = 256;
    public const long MaxArchiveBytes = 64L * 1024 * 1024;
    public const long MaxExpandedBytes = 64L * 1024 * 1024;
    public const long MaxSingleFileBytes = 32L * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] RequiredParts = ["window", "content", "sidebar", "composer", "composer.editor", "composer.toolbar", "dialog"];

    public static BasePackage ReadZipFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到原生基础包 ZIP。", path);
        }

        if (info.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("原生基础包 ZIP 超过 64 MB。 ");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadZipStream(stream);
    }

    public static BasePackage ReadZipBytes(ReadOnlyMemory<byte> zipBytes)
    {
        if (zipBytes.Length == 0 || zipBytes.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("原生基础包 ZIP 为空或超过 64 MB。 ");
        }

        using var stream = new MemoryStream(zipBytes.ToArray(), writable: false);
        return ReadZipStream(stream);
    }

    private static BasePackage ReadZipStream(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > MaxFiles + 1)
        {
            throw new InvalidDataException($"原生基础包条目数量无效；最多支持 {MaxFiles} 个资源文件。 ");
        }

        var files = new Dictionary<string, byte[]>(PathComparer);
        long expandedSize = 0;
        foreach (var entry in archive.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var path = NormalizeRelativePath(entry.FullName.TrimEnd('/', '\\'));
            if (isDirectory)
            {
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaxSingleFileBytes)
            {
                throw new InvalidDataException($"原生基础包文件“{path}”超过单文件大小上限。 ");
            }

            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > MaxExpandedBytes)
            {
                throw new InvalidDataException("原生基础包解压后总大小超过 64 MB。 ");
            }

            using var source = entry.Open();
            using var destination = new MemoryStream((int)entry.Length);
            CopyBounded(source, destination, entry.Length, path);
            if (destination.Length != entry.Length)
            {
                throw new InvalidDataException($"原生基础包文件“{path}”的实际大小与 ZIP 目录不一致。 ");
            }

            if (!files.TryAdd(path, destination.ToArray()))
            {
                throw new InvalidDataException($"原生基础包存在重复路径“{path}”。 ");
            }
        }

        if (!files.TryGetValue("base.json", out var manifestBytes))
        {
            throw new InvalidDataException("原生基础包 ZIP 根目录缺少 base.json。 ");
        }

        if (manifestBytes.Length is 0 or > MaxManifestBytes)
        {
            throw new InvalidDataException("base.json 为空或超过 1 MB。 ");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("base.json 格式错误。 ", exception);
        }

        using (document)
        {
            return CreatePackage(document.RootElement, files);
        }
    }

    private static BasePackage CreatePackage(JsonElement root, Dictionary<string, byte[]> archiveFiles)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("base.json 顶层必须是对象。 ");
        }

        if (GetInt32(root, "schemaVersion") != 1)
        {
            throw new InvalidDataException("仅支持 schemaVersion=1 的原生基础包。 ");
        }

        var id = GetRequiredString(root, "id");
        if (!IsIdentifier(id))
        {
            throw new InvalidDataException("原生基础包 ID 格式无效。 ");
        }

        var name = GetRequiredString(root, "name");
        var sourceVersion = GetRequiredString(root, "sourceVersion");
        var sourceAsarSha256 = GetRequiredString(root, "sourceAsarSha256");
        if (name.Length > 120 || sourceVersion.Length > 80 ||
            sourceAsarSha256.Length != 64 || !sourceAsarSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("base.json 的名称、版本或源 ASAR SHA-256 无效。 ");
        }

        var entry = NormalizeRelativePath(GetRequiredString(root, "entry"));
        var styles = ReadPathArray(root, "styles", requireCss: true);
        var parts = ReadStringArray(root, "parts");
        foreach (var requiredPart in RequiredParts)
        {
            if (!parts.Contains(requiredPart, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"原生基础包缺少 data-skin-part={requiredPart}。 ");
            }
        }

        if (!archiveFiles.TryGetValue("base.json", out _))
        {
            throw new InvalidDataException("base.json 缺失。 ");
        }

        var declarations = ReadFileDeclarations(root);
        var declaredPaths = declarations.Select(item => item.Path).ToHashSet(PathComparer);
        var actualPaths = archiveFiles.Keys.Where(path => !path.Equals("base.json", StringComparison.OrdinalIgnoreCase)).ToHashSet(PathComparer);
        if (!actualPaths.SetEquals(declaredPaths))
        {
            var missing = declaredPaths.Except(actualPaths, PathComparer).FirstOrDefault();
            var extra = actualPaths.Except(declaredPaths, PathComparer).FirstOrDefault();
            throw new InvalidDataException(missing is not null
                ? $"base.json 声明的资源“{missing}”不在 ZIP 中。 "
                : $"ZIP 包含未在 base.json 声明的资源“{extra}”。 ");
        }

        var verified = new Dictionary<string, byte[]>(PathComparer);
        foreach (var declaration in declarations)
        {
            var bytes = archiveFiles[declaration.Path];
            if (bytes.LongLength != declaration.Size)
            {
                throw new InvalidDataException($"原生基础包资源“{declaration.Path}”大小与 base.json 不一致。 ");
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actualHash.Equals(declaration.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"原生基础包资源“{declaration.Path}”SHA-256 与 base.json 不一致。 ");
            }

            verified.Add(declaration.Path, bytes);
        }

        if (!verified.ContainsKey(entry))
        {
            throw new InvalidDataException($"原生基础包入口“{entry}”不在 files[] 中。 ");
        }

        foreach (var style in styles)
        {
            if (!verified.TryGetValue(style, out var bytes))
            {
                throw new InvalidDataException($"原生基础包样式表“{style}”不在 files[] 中。 ");
            }

            try
            {
                _ = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"原生基础包样式表“{style}”不是有效的 UTF-8 文本。 ", exception);
            }
        }

        string entryHtml;
        try
        {
            entryHtml = StrictUtf8.GetString(verified[entry]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("原生基础包 HTML 入口不是有效的 UTF-8 文本。 ", exception);
        }

        if (Regex.IsMatch(entryHtml, "<script\\b", RegexOptions.IgnoreCase | RegexOptions.Singleline) ||
            !Regex.IsMatch(entryHtml, "<html\\b[^>]*\\bdata-theme\\s*=\\s*(['\"])light\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline) ||
            !Regex.IsMatch(entryHtml, "<html\\b[^>]*\\bdata-codex-window-type\\s*=\\s*(['\"])electron\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            throw new InvalidDataException("原生基础包 HTML 必须是无脚本的 Electron 静态入口，并声明 data-theme=light。 ");
        }

        foreach (var declaredPart in parts)
        {
            var marker = $"data-skin-part\\s*=\\s*(['\"]){Regex.Escape(declaredPart)}\\1";
            if (!Regex.IsMatch(entryHtml, marker, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                throw new InvalidDataException($"原生基础包 HTML 缺少 base.json 声明的 data-skin-part={declaredPart} 节点。 ");
            }
        }

        return new BasePackage(id, name, sourceVersion, sourceAsarSha256, entry, styles, parts, verified);
    }

    private static IReadOnlyList<(string Path, long Size, string Sha256)> ReadFileDeclarations(JsonElement root)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0 or > MaxFiles)
        {
            throw new InvalidDataException($"base.json 的 files 必须包含 1 到 {MaxFiles} 个资源声明。 ");
        }

        var result = new List<(string Path, long Size, string Sha256)>(files.GetArrayLength());
        var seen = new HashSet<string>(PathComparer);
        long total = 0;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("base.json 的每个 files[] 项都必须是对象。 ");
            }

            var path = NormalizeRelativePath(GetRequiredString(file, "path"));
            if (path.Equals("base.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("files[] 不能包含 base.json 自身。 ");
            }

            if (!seen.Add(path))
            {
                throw new InvalidDataException($"base.json 中存在重复资源路径“{path}”。 ");
            }

            var size = GetInt64(file, "size");
            var sha256 = GetRequiredString(file, "sha256");
            if (size < 0 || size > MaxSingleFileBytes || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"base.json 中“{path}”的大小或 SHA-256 无效。 ");
            }

            total = checked(total + size);
            if (total > MaxExpandedBytes)
            {
                throw new InvalidDataException("base.json 声明的资源总大小超过 64 MB。 ");
            }

            result.Add((path, size, sha256));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadPathArray(JsonElement root, string propertyName, bool requireCss)
    {
        var values = ReadStringArray(root, propertyName);
        if (values.Count == 0)
        {
            throw new InvalidDataException($"base.json 的 {propertyName} 不能为空。 ");
        }

        var normalized = values.Select(NormalizeRelativePath).ToArray();
        if (normalized.Distinct(PathComparer).Count() != normalized.Length ||
            (requireCss && normalized.Any(path => !Path.GetExtension(path).Equals(".css", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException($"base.json 的 {propertyName} 包含重复或无效路径。 ");
        }

        return normalized;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxFiles)
        {
            throw new InvalidDataException($"base.json 的 {propertyName} 必须是数组。 ");
        }

        var values = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException($"base.json 的 {propertyName} 只能包含非空字符串。 ");
            }

            values.Add(item.GetString()!);
        }

        return values;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("原生基础包资源路径必须是安全的相对 POSIX 路径。 ");
        }

        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Any(char.IsControl)))
        {
            throw new InvalidDataException("原生基础包资源路径包含无效片段。 ");
        }

        return string.Join('/', parts);
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"base.json 缺少有效的 {propertyName}。 ");
        }

        return property.GetString()!;
    }

    private static int GetInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"base.json 缺少有效的 {propertyName}。 ");
        }

        return value;
    }

    private static long GetInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw new InvalidDataException("base.json 的 files[].size 必须是有效整数。 ");
        }

        return value;
    }

    private static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static void CopyBounded(Stream source, Stream destination, long expectedSize, string path)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }

            copied += read;
            if (copied > MaxSingleFileBytes || copied > expectedSize)
            {
                throw new InvalidDataException($"原生基础包文件“{path}”实际大小超过允许范围。 ");
            }

            destination.Write(buffer, 0, read);
        }
    }
}
