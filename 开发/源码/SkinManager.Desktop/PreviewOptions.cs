namespace SkinManager.Desktop;

public sealed record PreviewOptions(
    string Scene = "home",
    string Appearance = "system",
    bool SidebarVisible = true,
    double? UserTransparency = null,
    bool ShowOriginalBase = false);
