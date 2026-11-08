using System.Text.Json;

namespace SkinManager.Runtime;

public sealed record RuntimeCompatibilityOptions(bool ForceSimplifiedChinese = false);

public enum RuntimeCompatibilityState
{
    Disabled,
    Applied,
    Pending,
    Unsupported,
    Failed,
    TimedOut
}

public sealed record RuntimeCompatibilityRequest
{
    public required string Endpoint { get; init; }
    public CodexProcessIdentity? ExpectedInstance { get; init; }
    public RuntimeCompatibilityOptions Options { get; init; } = new();
}

public sealed record RuntimeCompatibilityResult(
    RuntimeCompatibilityState State,
    string? Endpoint = null,
    string? Message = null,
    bool? IsDebugPortOpen = null,
    CodexProcessIdentity? ConnectedInstance = null);

public sealed class CompatibilitySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly object _sync = new();

    public CompatibilitySettingsStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkinManager",
            "compatibility-settings.json"));
    }

    public RuntimeCompatibilityOptions Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return new RuntimeCompatibilityOptions();

            var json = File.ReadAllText(_path);
            var options = JsonSerializer.Deserialize<RuntimeCompatibilityOptions>(json, JsonOptions);
            return options ?? throw new InvalidDataException("兼容性设置文件内容无效。");
        }
    }

    public void Save(RuntimeCompatibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
            WriteAtomically(_path, JsonSerializer.Serialize(options, JsonOptions));
    }

    internal static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("兼容性状态文件路径无效。");

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed record LocaleOverrideRestoreSnapshot(string? OriginalValue, string LastWrittenValue = "zh-CN");

internal sealed class LocaleOverrideSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly object _sync = new();

    public LocaleOverrideSnapshotStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkinManager",
            "locale-restore-state.json"));
    }

    public LocaleOverrideRestoreSnapshot? Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return null;

            var json = File.ReadAllText(_path);
            var snapshot = JsonSerializer.Deserialize<LocaleOverrideRestoreSnapshot>(json, JsonOptions);
            if (snapshot is null || !string.Equals(snapshot.LastWrittenValue, "zh-CN", StringComparison.Ordinal))
                throw new InvalidDataException("语言恢复状态文件内容无效。");
            return snapshot;
        }
    }

    public LocaleOverrideRestoreSnapshot CaptureIfMissing(string? originalValue)
    {
        lock (_sync)
        {
            if (File.Exists(_path))
            {
                var existing = JsonSerializer.Deserialize<LocaleOverrideRestoreSnapshot>(File.ReadAllText(_path), JsonOptions);
                if (existing is null || !string.Equals(existing.LastWrittenValue, "zh-CN", StringComparison.Ordinal))
                    throw new InvalidDataException("语言恢复状态文件内容无效。");
                return existing;
            }

            var snapshot = new LocaleOverrideRestoreSnapshot(originalValue);
            CompatibilitySettingsStore.WriteAtomically(_path, JsonSerializer.Serialize(snapshot, JsonOptions));
            return snapshot;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
    }
}
