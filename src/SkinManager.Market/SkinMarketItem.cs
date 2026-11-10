namespace SkinManager.Market;

public sealed record SkinMarketItem(
    string Id,
    string Name,
    string Version,
    string Appearance,
    string Description,
    string Author,
    string LicenseNotes,
    string? PreviewUrl,
    string DownloadUrl,
    string Sha256,
    long Size,
    int SkinFormatVersion,
    string MinimumManagerVersion,
    string UpdatedAt,
    string? ThumbnailUrl = null,
    string? FullDescription = null,
    string? Submitter = null,
    IReadOnlyList<SkinMarketTag>? Tags = null,
    string? DetailUrl = null,
    int LikeCount = 0,
    int CommentCount = 0,
    bool LikedByMe = false)
{
    public string EffectiveDescription => string.IsNullOrWhiteSpace(FullDescription) ? Description : FullDescription;
    public string? EffectiveThumbnailUrl => string.IsNullOrWhiteSpace(ThumbnailUrl) ? PreviewUrl : ThumbnailUrl;
}

public sealed record SkinMarketTag(string Id, string Name);

public sealed record SkinMarketPagination(int Page, int PerPage, int Total, int TotalPages);

public sealed record SkinMarketPage(
    IReadOnlyList<SkinMarketItem> Items,
    SkinMarketPagination Pagination,
    IReadOnlyList<SkinMarketTag>? AvailableTags = null);

public readonly record struct SkinMarketDownloadProgress(long BytesReceived, long TotalBytes);
