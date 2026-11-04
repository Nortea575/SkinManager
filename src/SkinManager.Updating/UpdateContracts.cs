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
    string Sha256);

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

    Task<DownloadedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default);
}
