namespace SkinManager.Runtime;

public enum CodexConfigReadabilityState
{
    Missing,
    Readable,
    Unreadable,
    InvalidPath
}

public sealed record CodexConfigReadabilityResult(string Path, CodexConfigReadabilityState State)
{
    public bool IsReadable => State == CodexConfigReadabilityState.Readable;
}

public interface ICodexConfigReadabilityService
{
    CodexConfigReadabilityResult Inspect();
}

public sealed class CodexConfigReadabilityService : ICodexConfigReadabilityService
{
    private readonly Func<string?> _codexHomeProvider;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _canReadFile;

    public CodexConfigReadabilityService()
        : this(GetEffectiveCodexHome, FileExists, CanReadFile)
    {
    }

    internal CodexConfigReadabilityService(
        Func<string?> codexHomeProvider,
        Func<string, bool> fileExists,
        Func<string, bool> canReadFile)
    {
        _codexHomeProvider = codexHomeProvider ?? throw new ArgumentNullException(nameof(codexHomeProvider));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _canReadFile = canReadFile ?? throw new ArgumentNullException(nameof(canReadFile));
    }

    public CodexConfigReadabilityResult Inspect()
    {
        string path;
        try
        {
            var home = _codexHomeProvider();
            if (string.IsNullOrWhiteSpace(home))
                return new(string.Empty, CodexConfigReadabilityState.InvalidPath);
            path = Path.GetFullPath(Path.Combine(home, "config.toml"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return new(string.Empty, CodexConfigReadabilityState.InvalidPath);
        }

        try
        {
            if (!_fileExists(path))
                return new(path, CodexConfigReadabilityState.Missing);
            return new(path, _canReadFile(path)
                ? CodexConfigReadabilityState.Readable
                : CodexConfigReadabilityState.Unreadable);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return new(path, CodexConfigReadabilityState.Unreadable);
        }
    }

    private static string? GetEffectiveCodexHome()
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
            return configuredHome;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile) ? null : Path.Combine(userProfile, ".codex");
    }

    private static bool CanReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.CanRead;
    }

    private static bool FileExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
