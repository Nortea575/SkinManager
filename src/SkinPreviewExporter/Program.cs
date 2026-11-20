namespace SkinPreviewExporter;

internal static class Program
{
    private const string Usage = """
        Usage: SkinPreviewExporter export --package <absolute-zip-path> --output <new-directory>
               SkinPreviewExporter emit-runtime-template --output <absolute-js-path>
               SkinPreviewExporter emit-runtime-assets --output <new-directory>
        """;

    private static int Main(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        try
        {
            if (TryParseExport(args, out var packagePath, out var outputPath))
            {
                var skin = PreviewExporter.Export(packagePath, outputPath);
                Console.Out.WriteLine($"OK {skin.Id} {skin.Version}");
                return 0;
            }

            if (args is ["emit-runtime-template" or "emit-runtime-assets", "--output", var assetPath]
                && Path.IsPathFullyQualified(assetPath))
            {
                var rendererVersion = args[0] == "emit-runtime-template"
                    ? PreviewExporter.EmitRuntimeTemplate(assetPath)
                    : PreviewExporter.EmitRuntimeAssets(assetPath);
                Console.Out.WriteLine($"OK {rendererVersion}");
                return 0;
            }

            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (PreviewExporter.OutputAlreadyExistsException)
        {
            Console.Error.WriteLine("请选择尚不存在的输出路径。");
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine("找不到皮肤 ZIP 文件。");
        }
        catch (InvalidDataException)
        {
            Console.Error.WriteLine("皮肤包格式无效或无法生成预览。");
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("无法读取输入包或写入预览目录。");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("读取或写入预览文件失败。");
        }
        catch (Exception)
        {
            Console.Error.WriteLine("生成预览失败。");
        }

        return 1;
    }

    private static bool TryParseExport(string[] args, out string packagePath, out string outputPath)
    {
        (packagePath, outputPath) = args switch
        {
            ["export", "--package", var package, "--output", var output] => (package, output),
            ["export", "--output", var output, "--package", var package] => (package, output),
            _ => (string.Empty, string.Empty)
        };

        return !string.IsNullOrWhiteSpace(packagePath) && !string.IsNullOrWhiteSpace(outputPath)
            && Path.IsPathFullyQualified(packagePath) && Path.IsPathFullyQualified(outputPath);
    }
}
