using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkinManager.Core;
using SkinManager.Rendering;

internal static class GifRuntimeChecks
{
    public static void Run()
    {
        var gifBytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "images",
            "transparent-animation.gif"));
        var package = SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
        {
            ["skin.json"] = Encoding.UTF8.GetBytes("""
                {
                  "formatVersion": 1,
                  "id": "gif-runtime-check",
                  "name": "GIF runtime check",
                  "version": "1.0.0",
                  "appearance": "light",
                  "background": {
                    "image": "assets/transparent-animation.gif",
                    "fit": "cover",
                    "focusX": 0.5,
                    "focusY": 0.5
                  }
                }
                """),
            ["assets/transparent-animation.gif"] = gifBytes
        });

        var script = SkinRuntimeCompiler.BuildRuntimeScript(package);
        var marker = "const runtimePackage = ";
        var jsonStart = script.IndexOf(marker, StringComparison.Ordinal);
        Require(jsonStart >= 0, "Generated runtime contains its package payload.");
        jsonStart += marker.Length;
        var payloadJson = ReadJsonObject(script, jsonStart);
        using var runtime = JsonDocument.Parse(payloadJson);

        var images = runtime.RootElement.GetProperty("images");
        var expectedKey = "sha256-" + Convert.ToHexString(SHA256.HashData(gifBytes)).ToLowerInvariant();
        var hasImage = images.TryGetProperty(expectedKey, out var imageValue);
        Require(images.EnumerateObject().Count() == 1 && hasImage,
            "Runtime caches the GIF once under a SHA-256 key derived only from its original bytes.");
        Require(imageValue.GetString() == $"data:image/gif;base64,{Convert.ToBase64String(gifBytes)}",
            "Runtime declares image/gif and embeds the original animation bytes without re-encoding.");

        var styles = runtime.RootElement.GetProperty("styles");
        foreach (var appearance in styles.EnumerateObject())
        {
            foreach (var scene in appearance.Value.EnumerateObject())
            {
                Require(scene.Value.GetProperty("background").GetProperty("imageUrl").GetString() == expectedKey,
                    "Color, opacity, focus, appearance, and scene payloads keep the same source image key.");
            }
        }

        Require(script.Contains("manager.blobUrls", StringComparison.Ordinal) &&
                script.Contains("manager.syncImageUrls", StringComparison.Ordinal) &&
                script.Contains("manager.backgroundElement", StringComparison.Ordinal) &&
                script.Contains("if (image.style.backgroundImage !== imageBackground)", StringComparison.Ordinal),
            "Runtime reuses the content-keyed Blob URL and existing background element when applying style updates.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL GIF runtime checks: {message}");
        }
    }

    private static string ReadJsonObject(string source, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidDataException("Generated runtime package JSON is incomplete.");
    }
}
