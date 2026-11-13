using System.IO.Compression;

namespace SkinManager.Core;

public sealed class SkinStore
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    public SkinStore(string skinsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skinsDirectory);
        DataDirectory = Path.GetFullPath(skinsDirectory);
        SkinsDirectory = DataDirectory;
    }

    public string DataDirectory { get; }
    public string SkinsDirectory { get; }
    public string? LastLoadWarning { get; private set; }

    public void Initialize()
    {
        Directory.CreateDirectory(SkinsDirectory);
        RejectReparsePoint(SkinsDirectory);
        RecoverTransactions();
    }

    public IReadOnlyList<SkinPackage> GetSkins()
    {
        LastLoadWarning = null;
        if (!Directory.Exists(SkinsDirectory))
        {
            return [];
        }

        var packages = new List<SkinPackage>();
        var unreadableCount = 0;
        string? firstUnreadableName = null;
        foreach (var directory in Directory.EnumerateDirectories(SkinsDirectory))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                RejectReparsePoint(directory);
                if (!SkinIdComparerIsValid(name))
                {
                    continue;
                }

                var package = SkinPackageReader.ReadFolder(directory);
                if (IdComparer.Equals(name, package.Id))
                {
                    packages.Add(package);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                unreadableCount++;
                firstUnreadableName ??= name;
            }
        }

        if (unreadableCount > 0)
        {
            LastLoadWarning = unreadableCount == 1
                ? AppLocalization.Format("皮肤库中的“{0}”无法读取，文件已保留；请重新导入有效皮肤包。", firstUnreadableName)
                : AppLocalization.Format("皮肤库中有 {0} 个皮肤无法读取（例如“{1}”），文件已保留；请重新导入有效皮肤包。",
                    unreadableCount, firstUnreadableName);
        }

        return packages.OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public IReadOnlyList<SkinCatalogEntry> GetCatalog()
    {
        LastLoadWarning = null;
        if (!Directory.Exists(SkinsDirectory))
        {
            return [];
        }

        RejectReparsePoint(SkinsDirectory);
        var entries = new List<SkinCatalogEntry>();
        var unreadableCount = 0;
        string? firstUnreadableName = null;
        foreach (var directory in Directory.EnumerateDirectories(SkinsDirectory))
        {
            var folderId = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(folderId) || folderId.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                RejectReparsePoint(directory);
                if (!SkinIdComparerIsValid(folderId))
                {
                    continue;
                }

                entries.Add(SkinPackageReader.ReadCatalogEntry(directory, folderId));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                unreadableCount++;
                firstUnreadableName ??= folderId;
            }
        }

        SetUnreadableWarning(unreadableCount, firstUnreadableName);
        return entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public SkinPackage LoadSkin(string id)
    {
        ValidateId(id);
        var directory = GetSkinDirectory(id);
        if (!Directory.Exists(directory))
        {
            throw new FileNotFoundException(AppLocalization.Get("所选皮肤不存在。"), directory);
        }

        RejectReparsePoint(directory);
        var package = SkinPackageReader.ReadFolder(directory);
        if (!IdComparer.Equals(id, package.Id))
        {
            throw new InvalidDataException(AppLocalization.Get("皮肤包 ID 与皮肤库目录名称不一致。 "));
        }

        return package;
    }

    public byte[]? ReadCatalogImage(SkinCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateId(entry.Id);
        if (entry.BackgroundImagePath is null)
        {
            return null;
        }

        var relativePath = ValidateCatalogImagePath(entry.BackgroundImagePath);
        var root = GetSkinDirectory(entry.Id);
        if (!Directory.Exists(root))
        {
            return null;
        }

        RejectReparsePoint(root);
        var path = ResolveInside(root, relativePath);
        var current = root;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!Directory.Exists(current))
            {
                return null;
            }

            RejectReparsePoint(current);
        }

        if (!File.Exists(path))
        {
            return null;
        }

        RejectReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > SkinPackageReader.MaxSingleFileBytes)
        {
            throw new InvalidDataException(AppLocalization.Get("皮肤缩略图资源为空或超过单文件大小上限。 "));
        }

        var bytes = File.ReadAllBytes(path);
        SkinPackageReader.ValidateImage(relativePath, bytes, $"$.files.{relativePath}");
        return bytes;
    }

    public SkinPackage ReadZip(string path) => SkinPackageReader.ReadZipFile(path);
    public SkinPackage ReadFolder(string path) => SkinPackageReader.ReadFolder(path);

    public bool ContainsId(string id)
    {
        ValidateId(id);
        return Directory.Exists(GetSkinDirectory(id)) || File.Exists(GetSkinDirectory(id));
    }

    public void Save(SkinPackage package, bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateId(package.Id);
        CommitPackage(package, replaceExisting);
    }

    public void Delete(string id)
    {
        ValidateId(id);
        var directory = GetSkinDirectory(id);
        if (!Directory.Exists(directory) && !File.Exists(directory))
        {
            return;
        }

        if (File.Exists(directory) && !Directory.Exists(directory))
        {
            File.Delete(directory);
            return;
        }

        SafeDeleteTree(directory);
    }

    public void ExportZip(string id, string destinationPath, bool overwriteExisting = false)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var package = LoadSkin(id);
        var destination = Path.GetFullPath(destinationPath);
        if (Directory.Exists(destination) || (File.Exists(destination) && !overwriteExisting))
        {
            throw new IOException(AppLocalization.Get("目标文件已存在；导出不会覆盖未经确认的文件。 "));
        }

        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(AppLocalization.Get("导出目标文件夹不存在。 "));
        }

        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var pair in package.Files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using var output = entry.Open();
                    output.Write(pair.Value, 0, pair.Value.Length);
                }
            }

            if (File.Exists(destination))
            {
                if (!overwriteExisting)
                {
                    throw new IOException(AppLocalization.Get("目标文件已存在；导出不会覆盖未经确认的文件。 "));
                }

                File.Replace(temporary, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, destination, overwrite: false);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void CommitPackage(SkinPackage package, bool replaceExisting)
    {
        Directory.CreateDirectory(SkinsDirectory);
        var destination = GetSkinDirectory(package.Id);
        var exists = Directory.Exists(destination) || File.Exists(destination);
        if (exists && !replaceExisting)
        {
            throw new DuplicateSkinException(package.Id);
        }

        if (Directory.Exists(destination))
        {
            RejectReparsePoint(destination);
        }

        var temporary = Path.Combine(SkinsDirectory, $".{package.Id}.pending-{Guid.NewGuid():N}");
        var backup = Path.Combine(SkinsDirectory, $".{package.Id}.backup-{Guid.NewGuid():N}");
        var oldMoved = false;
        try
        {
            Directory.CreateDirectory(temporary);
            foreach (var pair in package.Files)
            {
                var target = ResolveInside(temporary, pair.Key);
                var targetDirectory = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(targetDirectory);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(pair.Value, 0, pair.Value.Length);
            }

            _ = SkinPackageReader.ReadFolder(temporary);
            if (exists)
            {
                if (File.Exists(destination) && !Directory.Exists(destination))
                {
                    throw new IOException(AppLocalization.Get("皮肤库中存在与皮肤 ID 同名的文件。 "));
                }

                Directory.Move(destination, backup);
                oldMoved = true;
            }

            try
            {
                Directory.Move(temporary, destination);
            }
            catch
            {
                if (oldMoved && Directory.Exists(backup) && !Directory.Exists(destination))
                {
                    Directory.Move(backup, destination);
                    oldMoved = false;
                }

                throw;
            }

            if (oldMoved && Directory.Exists(backup))
            {
                try
                {
                    SafeDeleteTree(backup);
                }
                catch (IOException)
                {
                    // The replacement is committed; a leftover backup is ignored and recovered on next start.
                }
                catch (UnauthorizedAccessException)
                {
                    // The replacement is committed; a leftover backup is ignored and recovered on next start.
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                SafeDeleteTree(temporary);
            }

            if (oldMoved && Directory.Exists(backup) && !Directory.Exists(destination))
            {
                Directory.Move(backup, destination);
            }
        }
    }

    private void RecoverTransactions()
    {
        foreach (var directory in Directory.EnumerateDirectories(SkinsDirectory))
        {
            var name = Path.GetFileName(directory);
            if (TryGetTransactionId(name, ".pending-", out _))
            {
                SafeDeleteTree(directory);
                continue;
            }

            if (!TryGetTransactionId(name, ".backup-", out var id))
            {
                continue;
            }

            var destination = GetSkinDirectory(id);
            if (!Directory.Exists(destination) && !File.Exists(destination))
            {
                Directory.Move(directory, destination);
            }
            else
            {
                SafeDeleteTree(directory);
            }
        }
    }

    private static bool TryGetTransactionId(string directoryName, string marker, out string id)
    {
        id = string.Empty;
        if (!directoryName.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var markerIndex = directoryName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 1)
        {
            return false;
        }

        var candidateId = directoryName[1..markerIndex];
        var suffix = directoryName[(markerIndex + marker.Length)..];
        if (!SkinIdComparerIsValid(candidateId) || !Guid.TryParseExact(suffix, "N", out _))
        {
            return false;
        }

        id = candidateId;
        return true;
    }

    private string GetSkinDirectory(string id) => Path.Combine(SkinsDirectory, id);

    private static string ResolveInside(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(AppLocalization.Get("资源路径超出了皮肤目录。 "));
        }

        return fullPath;
    }

    private static void ValidateId(string id)
    {
        if (!SkinIdComparerIsValid(id))
        {
            throw new InvalidDataException(AppLocalization.Get("皮肤 ID 无效。 "));
        }
    }

    private static bool SkinIdComparerIsValid(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
        id[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        id is not "." and not "..";

    private static string ValidateCatalogImagePath(string path)
    {
        var normalized = SkinPackageReader.NormalizeRelativePath(path, "$.background.image");
        if (!SkinPackageReader.IsSupportedImagePath(normalized))
        {
            throw new InvalidDataException(AppLocalization.Get("背景图片只支持 PNG、JPG、JPEG 或 GIF。 "));
        }

        return normalized;
    }

    private void SetUnreadableWarning(int unreadableCount, string? firstUnreadableName)
    {
        if (unreadableCount > 0)
        {
            LastLoadWarning = unreadableCount == 1
                ? AppLocalization.Format("皮肤库中的“{0}”无法读取，文件已保留；请重新导入有效皮肤包。", firstUnreadableName)
                : AppLocalization.Format("皮肤库中有 {0} 个皮肤无法读取（例如“{1}”），文件已保留；请重新导入有效皮肤包。",
                    unreadableCount, firstUnreadableName);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(AppLocalization.Get("皮肤库路径不能是符号链接或重解析点。 "));
        }
    }

    private static void SafeDeleteTree(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                SafeDeleteTree(entry);
            }

            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }
}
