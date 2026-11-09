namespace SkinManager.Updating;

public enum UpdateCheckState
{
    UpToDate,
    Available,
    NotPublished,
    PackageNotReady
}

public sealed record UpdateRelease(
    Version Version,
    string Tag,
    string Notes,
    Uri DownloadUri,
    long Size,
    string Sha256)
{
    /// <summary>The full semantic version displayed to users and used to name cached installers.</summary>
    public string DisplayVersion => SemanticVersion.TryParseTag(Tag, out var version)
        ? version.ToString()
        : FormatVersion(Version);

    /// <summary>Whether GitHub marked this release as a prerelease or its tag contains a prerelease label.</summary>
    public bool IsPrerelease { get; init; } =
        SemanticVersion.TryParseTag(Tag, out var semanticVersion) && semanticVersion.IsPrerelease;

    private static string FormatVersion(Version version) =>
        $"{version.Major}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
}

public sealed record UpdateCheckRequest(
    Version CurrentVersion,
    bool IncludePrereleases = false,
    string? CurrentVersionTag = null);

public sealed record UpdateCheckResult(UpdateCheckState State, UpdateRelease? Release = null);

public sealed record DownloadedUpdate(UpdateRelease Release, string FilePath);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double? Percent => TotalBytes <= 0
        ? null
        : Math.Clamp((double)BytesReceived / TotalBytes * 100, 0, 100);
}

public interface IUpdateClient
{
    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);

    Task<UpdateCheckResult> CheckAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CurrentVersion);
        return CheckAsync(request.CurrentVersion, cancellationToken);
    }

    Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default);
}
