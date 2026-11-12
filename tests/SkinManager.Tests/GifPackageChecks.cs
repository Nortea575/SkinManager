using System.Text;
using System.Text.Json.Nodes;
using SkinManager.Core;

internal static class GifPackageChecks
{
    public static int Run(string testRoot)
    {
        var gifPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "images", "transparent-animation.gif");
        var gifBytes = File.ReadAllBytes(gifPath);
        var info = SkinPackageReader.ReadGifInfo(gifBytes);
        Require(info.Width == 16 && info.Height == 16 &&
                info.FirstFrameLeft == 2 && info.FirstFrameTop == 2 &&
                info.FirstFrameWidth == 12 && info.FirstFrameHeight == 12,
            "GIF parser reads the 16×16 logical canvas and the first local frame rectangle.");
        Require(info.FirstFrameHasTransparency && info.FirstFrameTransparentIndex == 0,
            "GIF parser retains the first frame transparency control.");
        Require(SkinPackageReader.IsSupportedImagePath("assets/animation.GIF") &&
                SkinPackageReader.GetImageMediaType("assets/animation.gif") == "image/gif",
            "GIF joins the shared image extension and MIME mapping.");

        var imported = BuildPackage(gifBytes, "assets/transparent-animation.gif", "gif-import");
        Require(imported.ImageBytes is not null && imported.ImageBytes.SequenceEqual(gifBytes),
            "Package import retains every original GIF byte.");

        var draftBase = SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
        {
            ["skin.json"] = Encoding.UTF8.GetBytes(Manifest("gif-draft", null))
        });
        var draft = SkinDraft.FromPackage(draftBase);
        draft.SetBackgroundImage(gifBytes, "transparent-animation.gif");
        var drafted = draft.BuildPackage();
        Require(drafted.ImagePath is { } draftedImagePath &&
                Path.GetExtension(draftedImagePath).Equals(".gif", StringComparison.OrdinalIgnoreCase) &&
                drafted.ImageBytes is not null && drafted.ImageBytes.SequenceEqual(gifBytes),
            "SkinDraft accepts a GIF and stores the original animated resource bytes.");

        var store = new SkinStore(Path.Combine(testRoot, "gif-store"));
        store.Initialize();
        store.Save(imported, replaceExisting: false);
        var exportPath = Path.Combine(testRoot, "gif-export.zip");
        store.ExportZip(imported.Id, exportPath);
        var reimported = SkinPackageReader.ReadZipFile(exportPath);
        Require(reimported.ImageBytes is not null && reimported.ImageBytes.SequenceEqual(gifBytes),
            "Store save and ZIP export/import preserve the GIF animation bytes unchanged.");

        var truncated = gifBytes[..^1];
        Require(RejectsImage(truncated, "assets/truncated.gif", "gif-truncated"),
            "GIF import rejects a file missing its trailer.");
        Require(RejectsImage(gifBytes, "assets/renamed.png", "gif-pseudo-extension"),
            "GIF import rejects GIF bytes presented under a PNG extension.");
        Require(RejectsImage(Encoding.ASCII.GetBytes("GIF89a"), "assets/header-only.gif", "gif-header-only"),
            "GIF import rejects a header-only pseudo image.");

        var tooWide = gifBytes.ToArray();
        tooWide[6] = 0x81;
        tooWide[7] = 0x3e;
        Require(RejectsImage(tooWide, "assets/oversized.gif", "gif-oversized"),
            "GIF import enforces the existing 16,000-pixel dimension limit on its logical canvas.");

        return 10;
    }

    private static SkinPackage BuildPackage(byte[] imageBytes, string imagePath, string id) =>
        SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
        {
            ["skin.json"] = Encoding.UTF8.GetBytes(Manifest(id, imagePath)),
            [imagePath] = imageBytes
        });

    private static bool RejectsImage(byte[] imageBytes, string imagePath, string id)
    {
        try
        {
            _ = BuildPackage(imageBytes, imagePath, id);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static string Manifest(string id, string? imagePath)
    {
        var manifest = new JsonObject
        {
            ["formatVersion"] = 1,
            ["id"] = id,
            ["name"] = "GIF package check",
            ["version"] = "1.0.0",
            ["appearance"] = "light"
        };
        if (imagePath is not null)
        {
            manifest["background"] = new JsonObject
            {
                ["image"] = imagePath,
                ["fit"] = "contain"
            };
        }

        return manifest.ToJsonString();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL GIF package checks: {message}");
        }
    }
}
