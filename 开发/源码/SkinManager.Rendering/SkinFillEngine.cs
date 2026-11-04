using System.Reflection;

namespace SkinManager.Rendering;

internal static class SkinFillEngine
{
    private const string ResourceName = "SkinManager.Rendering.SkinFillEngine.js";
    private static readonly Lazy<string> SourceValue = new(LoadSource, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string Source => SourceValue.Value;

    private static string LoadSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"找不到内嵌资源 {ResourceName}。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
