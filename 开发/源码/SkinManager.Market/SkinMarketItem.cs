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
    string UpdatedAt);

public sealed record SkinMarketPagination(int Page, int PerPage, int Total, int TotalPages);

public sealed record SkinMarketPage(IReadOnlyList<SkinMarketItem> Items, SkinMarketPagination Pagination);

public readonly record struct SkinMarketDownloadProgress(long BytesReceived, long TotalBytes);
