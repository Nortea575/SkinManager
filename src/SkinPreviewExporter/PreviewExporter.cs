using System.Text.Json;
using SkinManager.Core;
using SkinManager.Rendering;

namespace SkinPreviewExporter;

internal static class PreviewExporter
{
    private const int FormatVersion = 1;
    private const string BaseVersion = "26.1027.3384.0";
    private const string RendererVersion = "1.1.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static PreviewSkin Export(string packageZip, string outputDirectory) =>
        WriteNewDirectory(outputDirectory, staging =>
        {
            var package = SkinPackageReader.ReadZipFile(packageZip);
            var runtime = SkinRuntimeCompiler.BuildPreviewRuntimePackage(package, (imageKey, imagePath, bytes) =>
            {
                var filename = imageKey + Path.GetExtension(imagePath).ToLowerInvariant();
                var imagesDirectory = Path.Combine(staging, "images");
                Directory.CreateDirectory(imagesDirectory);
                File.WriteAllBytes(Path.Combine(imagesDirectory, filename), bytes);
                return $"images/{filename}";
            });
            var skin = new PreviewSkin(package.Id, package.Name, package.Version, runtime.Appearance);
            var manifest = new PreviewManifest(FormatVersion, BaseVersion, RendererVersion, skin, runtime);
            WriteJson(Path.Combine(staging, "preview.json"), manifest);
            return skin;
        });

    internal static string EmitRuntimeAssets(string outputDirectory) =>
        WriteNewDirectory(outputDirectory, staging =>
        {
            var runtimeDirectory = Path.Combine(staging, "runtime", RendererVersion);
            Directory.CreateDirectory(runtimeDirectory);
            WriteRuntimeTemplate(Path.Combine(runtimeDirectory, "runtime.js"));
            var metadata = new PreviewRenderer(FormatVersion, BaseVersion, RendererVersion);
            WriteJson(Path.Combine(staging, "renderer.json"), metadata);
            return RendererVersion;
        });

    internal static string EmitRuntimeTemplate(string outputPath)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var staging = PrepareStagingPath(fullOutput);
        try
        {
            WriteRuntimeTemplate(staging);
            File.Move(staging, fullOutput);
            return RendererVersion;
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static T WriteNewDirectory<T>(string outputDirectory, Func<string, T> populate)
    {
        var fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var staging = PrepareStagingPath(fullOutput);
        Directory.CreateDirectory(staging);
        try
        {
            var result = populate(staging);
            Directory.Move(staging, fullOutput);
            return result;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static string PrepareStagingPath(string fullOutput)
    {
        if (Directory.Exists(fullOutput) || File.Exists(fullOutput))
        {
            throw new OutputAlreadyExistsException();
        }

        var parent = Path.GetDirectoryName(fullOutput) ?? throw new IOException("输出路径无效。");
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, $".skin-preview-{Guid.NewGuid():N}");
    }

    private static void WriteRuntimeTemplate(string path) => File.WriteAllText(path, SkinRuntimeCompiler.BuildPreviewRuntimeTemplate());

    private static void WriteJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n");

    internal sealed class OutputAlreadyExistsException : IOException
    {
    }

    private sealed record PreviewManifest(
        int FormatVersion,
        string BaseVersion,
        string RendererVersion,
        PreviewSkin Skin,
        PreviewRuntimePackage Runtime);

    private sealed record PreviewRenderer(int FormatVersion, string BaseVersion, string RendererVersion);
}

internal sealed record PreviewSkin(string Id, string Name, string Version, string Appearance);
