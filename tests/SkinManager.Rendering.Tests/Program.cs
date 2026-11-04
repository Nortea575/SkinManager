using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkinManager.Core;
using SkinManager.Rendering;

var ayanamiDirectory = Path.Combine(AppContext.BaseDirectory, "skin", "ayanami");
var package = SkinPackageReader.ReadFolder(ayanamiDirectory);
var runtimeScript = SkinRuntimeCompiler.BuildRuntimeScript(package);
var sayaPackage = SkinPackageReader.ReadFolder(Path.Combine(AppContext.BaseDirectory, "skin", "saya"));
var sayaRuntimeScript = SkinRuntimeCompiler.BuildRuntimeScript(sayaPackage);
var nativeCaptionLog = new List<string>
{
    "离线 native caption 配色验证：仅构建器与 fake DOM/bridge；未启动 Codex、未注入、未改宿主主题。",
    "皮肤\t宿主主题\t宿主 glyph\t底板色\t对比度"
};
nativeCaptionLog.Add(VerifyNativeCaptionPlan(
    SkinResolver.Resolve(package, SkinAppearance.Light, SkinScene.Home),
    "Ayanami",
    forLightSymbols: "#5875A7",
    forDarkSymbols: "#F5F7FB"));
nativeCaptionLog.Add(VerifyNativeCaptionPlan(
    SkinResolver.Resolve(sayaPackage, SkinAppearance.Dark, SkinScene.Home),
    "Saya",
    forLightSymbols: "#13202B",
    forDarkSymbols: "#91C7B0"));
var defaultComposerOpacity = NativeSkinStyleBuilder.Build(
    SkinResolver.Resolve(package, SkinAppearance.Light, SkinScene.Home),
    imageUrl: null).FillOpacity["composer"].Default;
Require(defaultComposerOpacity is double defaultAlpha && Math.Abs(defaultAlpha - 0.25) < 0.0001,
    "Ayanami's theme default renders the composer with 25% fill alpha (75% transparency).");
using (var runtime = JsonDocument.Parse(ExtractRuntimePackage(runtimeScript)))
{
    var runtimeRoot = runtime.RootElement;
    var styles = runtimeRoot.GetProperty("styles");
    var images = runtimeRoot.GetProperty("images");
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    Require(runtimeRoot.GetProperty("appearance").GetString() == "light", "Runtime carries the skin's fixed light appearance.");
    Require(styles.EnumerateObject().Count() == 2, "Runtime includes both actual themes.");
    Require(images.EnumerateObject().Count() == 1, "The same Ayanami image is embedded once by content hash.");
    Require(!runtimeScript.Contains("https://codex-preview.invalid", StringComparison.Ordinal), "Live runtime does not depend on preview virtual URLs.");

    foreach (var (themeName, appearance) in new[]
    {
        ("light", SkinAppearance.Light),
        ("dark", SkinAppearance.Dark),
    })
    {
        foreach (var (sceneName, scene) in new[]
        {
            ("home", SkinScene.Home),
            ("conversation", SkinScene.Conversation),
        })
        {
            var resolved = SkinResolver.Resolve(package, appearance, scene);
            var resolvedImageKey = GetImageKey(resolved.Background);
            var expected = NativeSkinStyleBuilder.Build(resolved, resolvedImageKey);
            var expectedJson = JsonSerializer.Serialize(expected, jsonOptions);
            var runtimeJson = styles.GetProperty(themeName).GetProperty(sceneName).GetRawText();
            Require(expectedJson == runtimeJson, $"{themeName}/{sceneName} runtime output matches NativeSkinStyleBuilder.");
        }
    }

    var imagePath = package.ImagePath ?? throw new InvalidOperationException("Ayanami test skin has no base image.");
    var imageBytes = package.Files[imagePath];
    var imageKey = "sha256-" + Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
    var imageValue = images.GetProperty(imageKey).GetString();
    Require(imageValue == $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}", "The cached image contains the original package bytes.");
}

Require(GetComposerOpacity(package, 0) == 1, "Zero user transparency keeps surfaces fully opaque.");
Require(GetComposerOpacity(package, 100) == 0, "One hundred percent user transparency makes surfaces transparent.");

var panelPayload = NativeSkinStyleBuilder.Build(
    SkinResolver.Resolve(package, SkinAppearance.Light, SkinScene.Home, 0.5),
    imageUrl: null);
Require(panelPayload.FillOpacity["content"].NoFill, "Ayanami's none background remains unfilled.");

var temporaryVariantDirectory = CreateTransparentAyanamiVariant(ayanamiDirectory);
try
{
    var transparentPackage = SkinPackageReader.ReadFolder(temporaryVariantDirectory);
    var transparentPayload = NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(transparentPackage, SkinAppearance.Light, SkinScene.Home, 0.5),
        imageUrl: null);
    var content = transparentPayload.FillOpacity["content"];
    Require(!content.NoFill && content.HasExplicitBackground, "An alpha-zero background stays an explicit transparent surface.");
    Require(transparentPayload.Css.Contains("background-color:rgba(0, 0, 0, 0)", StringComparison.Ordinal), "An alpha-zero color stays transparent in generated CSS.");
}
finally
{
    Directory.Delete(temporaryVariantDirectory, recursive: true);
}

var transparentCaptionDirectory = Path.Combine(Path.GetTempPath(), "SkinManagerRenderingTests", Guid.NewGuid().ToString("N"));
var imageOnlyCaptionDirectory = Path.Combine(Path.GetTempPath(), "SkinManagerRenderingTests", Guid.NewGuid().ToString("N"));
try
{
    var transparentCaption = CreateCaptionColorFixture(
        transparentCaptionDirectory,
        windowColor: "rgba(47, 93, 133, 0.45)",
        accentColor: "rgba(210, 160, 60, 0.5)");
    var transparentCaptionPlan = NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(transparentCaption, SkinAppearance.Light, SkinScene.Home),
        imageUrl: null).NativeCaption;
    Require(transparentCaptionPlan?.ForLightSymbols == "#2F5D85" && transparentCaptionPlan.ForDarkSymbols == "#D2A03C",
        "Translucent skin colors remain usable when no opaque surface color is defined.");

    var imageOnlyCaption = CreateImageOnlyCaptionFixture(imageOnlyCaptionDirectory, ayanamiDirectory);
    var imageOnlyCaptionPlan = NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(imageOnlyCaption, SkinAppearance.Light, SkinScene.Home),
        imageUrl: null).NativeCaption;
    Require(imageOnlyCaptionPlan?.ForLightSymbols == "#1F1F1F" && imageOnlyCaptionPlan.ForDarkSymbols == "#FFFFFF",
        "An image-only skin with no usable color keeps the native symbols readable through the compatibility fallback.");
}
finally
{
    if (Directory.Exists(transparentCaptionDirectory)) Directory.Delete(transparentCaptionDirectory, recursive: true);
    if (Directory.Exists(imageOnlyCaptionDirectory)) Directory.Delete(imageOnlyCaptionDirectory, recursive: true);
}

var nativePackage = SkinPackageReader.ReadFolder(Path.Combine(AppContext.BaseDirectory, "skin", "native"));
var nativeRuntimeScript = SkinRuntimeCompiler.BuildRuntimeScript(nativePackage);
using (var nativeRuntime = JsonDocument.Parse(ExtractRuntimePackage(nativeRuntimeScript)))
{
    Require(nativeRuntime.RootElement.GetProperty("appearance").GetString() == "system", "Native runtime retains system appearance.");
    Require(!nativeRuntime.RootElement.GetProperty("enabled").GetBoolean(), "An empty native package does not activate skin overrides.");
    foreach (var theme in nativeRuntime.RootElement.GetProperty("styles").EnumerateObject())
    {
        foreach (var scene in theme.Value.EnumerateObject())
        {
            var payload = scene.Value;
            Require(payload.GetProperty("css").GetString() == string.Empty, "An empty native package emits no CSS overrides.");
            Require(payload.GetProperty("fillOpacity").EnumerateObject().Count() == 0, "An empty native package emits no forced surface fills.");
            Require(payload.GetProperty("background").ValueKind == JsonValueKind.Null, "An empty native package does not replace the native background.");
            Require(payload.GetProperty("nativeCaption").ValueKind == JsonValueKind.Object, "A skin package without color definitions carries the native readability fallback plan.");
        }
    }
}

var emptyDarkDirectory = Path.Combine(Path.GetTempPath(), "SkinManagerRenderingTests", Guid.NewGuid().ToString("N"));
var emptyDarkRuntimeScript = string.Empty;
try
{
    Directory.CreateDirectory(emptyDarkDirectory);
    File.WriteAllText(Path.Combine(emptyDarkDirectory, "skin.json"), """
        {
          "formatVersion": 1,
          "id": "empty-dark",
          "name": "Empty dark",
          "version": "1.0.0",
          "appearance": "dark"
        }
        """);
    var emptyDark = SkinPackageReader.ReadFolder(emptyDarkDirectory);
    emptyDarkRuntimeScript = SkinRuntimeCompiler.BuildRuntimeScript(emptyDark);
    using var emptyDarkRuntime = JsonDocument.Parse(ExtractRuntimePackage(emptyDarkRuntimeScript));
    Require(emptyDarkRuntime.RootElement.GetProperty("appearance").GetString() == "dark", "Runtime carries a fixed dark appearance without style overrides.");
    Require(emptyDarkRuntime.RootElement.GetProperty("enabled").GetBoolean(), "A fixed appearance alone enables the runtime overlay.");
    foreach (var theme in emptyDarkRuntime.RootElement.GetProperty("styles").EnumerateObject())
    {
        foreach (var scene in theme.Value.EnumerateObject())
        {
            var payload = scene.Value;
            Require(payload.GetProperty("css").GetString() == string.Empty, "A fixed-appearance-only package emits no CSS overrides.");
            Require(payload.GetProperty("fillOpacity").EnumerateObject().Count() == 0, "A fixed-appearance-only package emits no forced surface fills.");
            Require(payload.GetProperty("background").ValueKind == JsonValueKind.Null, "A fixed-appearance-only package does not replace the native background.");
            var nativeCaption = payload.GetProperty("nativeCaption");
            Require(nativeCaption.GetProperty("forLightSymbols").GetString() == "#1F1F1F" &&
                    nativeCaption.GetProperty("forDarkSymbols").GetString() == "#FFFFFF",
                "A fixed-appearance-only package without skin colors uses the native readability fallback plan.");
        }
    }
}
finally
{
    Directory.Delete(emptyDarkDirectory, recursive: true);
}

NativeCaptionBackgroundTests.VerifyRuntime(
    runtimeScript,
    expectedForDarkSymbolsBackground: "#F5F7FB",
    expectedForLightSymbolsBackground: "#5875A7",
    skinChangeRuntimeScript: sayaRuntimeScript,
    expectedChangedForDarkSymbolsBackground: "#91C7B0",
    expectedChangedForLightSymbolsBackground: "#13202B",
    noCaptionRuntimeScript: nativeRuntimeScript);

var captionLogDirectory = Path.Combine(FindRepositoryRoot(), "artifacts", "test-results", "logs");
Directory.CreateDirectory(captionLogDirectory);
var captionLogPath = Path.Combine(captionLogDirectory, "NativeCaptionBackground-1.0.0.log");
File.WriteAllLines(captionLogPath, nativeCaptionLog, new System.Text.UTF8Encoding(false));

var invalidTransparencyRejected = false;
try
{
    _ = SkinRuntimeCompiler.BuildRuntimeScript(package, 101);
}
catch (ArgumentOutOfRangeException)
{
    invalidTransparencyRejected = true;
}
Require(invalidTransparencyRejected, "Transparency outside 0..100 is rejected.");

Require(runtimeScript.Contains("main[data-app-shell-main-surface]", StringComparison.Ordinal), "The runtime recognizes the extracted native content region.");
Require(runtimeScript.Contains("article[data-message-author-role", StringComparison.Ordinal), "The runtime recognizes extracted native message roles.");
Require(runtimeScript.Contains("_ComposerLayoutRoot_", StringComparison.Ordinal), "The runtime recognizes the extracted native composer root.");

var previewPayload = NativeSkinStyleBuilder.Build(
    SkinResolver.Resolve(package, SkinAppearance.Light, SkinScene.Home),
    imageUrl: null);
NativeFooterBackdropTests.VerifyCss(previewPayload.Css);
NativeFooterBackdropTests.VerifyRuntimeUpgrade(runtimeScript);
var chatTextCss = NativeSkinStyleBuilder.Build(
    SkinResolver.Resolve(package, SkinAppearance.Light, SkinScene.Conversation),
    imageUrl: null).Css;
Require(chatTextCss.Contains(":root[data-theme] [data-markdown-text-style=\"assistant-message\"]", StringComparison.Ordinal),
    "Assistant Markdown text aliases are scoped to the native assistant-message root.");
Require(chatTextCss.Contains("--color-text-prose:#202b3e !important", StringComparison.Ordinal) &&
        chatTextCss.Contains("--color-text-emphasis:#202b3e !important", StringComparison.Ordinal),
    "Primary skin ink feeds the native prose and emphasis variables used by TextBase.");
Require(chatTextCss.Contains("--color-text-secondary:#61708b !important", StringComparison.Ordinal),
    "Secondary skin ink feeds the native Caption variable inside Chat assistant content.");
Require(chatTextCss.Contains("[class*=\"_TextBase_\"]", StringComparison.Ordinal) &&
        chatTextCss.Contains(":not(:where(a, a *, [role=\"link\"]", StringComparison.Ordinal) &&
        chatTextCss.Contains("[data-d-component=\"link\"]", StringComparison.Ordinal) &&
        chatTextCss.Contains("code, code *, pre, pre *", StringComparison.Ordinal),
    "Chat text aliases target the stable TextBase class prefix while excluding link and code trees.");
Require(!chatTextCss.Contains("TextBase_1ccqq_1", StringComparison.Ordinal) &&
        !chatTextCss.Contains("data-markdown-text-style=\"user-message\"", StringComparison.Ordinal) &&
        !chatTextCss.Contains("[data-markdown-text-style=\"assistant-message\"] [data-theme]", StringComparison.Ordinal),
    "Chat text rules do not depend on hashed TextBase classes or user-message selectors.");

var chatTextFixtureDirectory = Path.Combine(Path.GetTempPath(), "SkinManagerRenderingTests", Guid.NewGuid().ToString("N"));
try
{
    var secondaryOnly = CreateChatTextFixture(chatTextFixtureDirectory, primary: null, secondary: "#5f7088");
    var secondaryOnlyCss = NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(secondaryOnly, SkinAppearance.Dark, SkinScene.Conversation),
        imageUrl: null).Css;
    Require(secondaryOnlyCss.Contains("--color-text-secondary:#5f7088 !important", StringComparison.Ordinal) &&
            !secondaryOnlyCss.Contains("--color-text-prose:", StringComparison.Ordinal),
        "A secondary-only skin updates Chat secondary text without inventing primary text.");

    var explicitAssistantColor = CreateChatTextFixture(
        chatTextFixtureDirectory,
        primary: "#202b3e",
        secondary: "#61708b",
        assistantColor: "#a42e84");
    var explicitAssistantCss = NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(explicitAssistantColor, SkinAppearance.Light, SkinScene.Conversation),
        imageUrl: null).Css;
    Require(explicitAssistantCss.Contains("--color-text-primary:#202b3e !important", StringComparison.Ordinal) &&
            explicitAssistantCss.Contains("--color-text-prose:#a42e84 !important", StringComparison.Ordinal),
        "An explicit message.assistant color takes precedence inside Chat while the global primary token remains intact.");
}
finally
{
    if (Directory.Exists(chatTextFixtureDirectory))
    {
        Directory.Delete(chatTextFixtureDirectory, recursive: true);
    }
}

Require(NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(nativePackage, SkinAppearance.Light, SkinScene.Conversation),
        imageUrl: null).Css == string.Empty,
    "The empty Native skin emits no Chat text aliases.");
VerifyJavaScriptSyntax(runtimeScript, "runtime");
VerifyJavaScriptSyntax(SkinRuntimeCompiler.BuildRemoveScript(), "remove");
var previewApplyScript = NativeSkinStyleBuilder.BuildApplyScript(previewPayload);
VerifyJavaScriptSyntax(previewApplyScript, "preview apply");
SharedFillEngineTests.Verify(runtimeScript, previewApplyScript);
var markerFontPackage = SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
{
    ["skin.json"] = System.Text.Encoding.UTF8.GetBytes("""
    {"formatVersion":1,"id":"marker-font","name":"模板标记字体","version":"1","appearance":"light",
     "tokens":{"font.family":"__CODEX_ADAPTER_JSON__"},"components":{"composer":{"fontFamily":"{font.family}"}}}
    """)
});
var markerRuntimeScript = SkinRuntimeCompiler.BuildRuntimeScript(markerFontPackage);
var markerPreviewScript = NativeSkinStyleBuilder.BuildApplyScript(
    NativeSkinStyleBuilder.Build(SkinResolver.Resolve(markerFontPackage, SkinAppearance.Light, SkinScene.Home), null));
VerifyJavaScriptSyntax(markerRuntimeScript, "runtime with template-marker font family");
VerifyJavaScriptSyntax(markerPreviewScript, "preview with template-marker font family");
using (var markerRuntime = JsonDocument.Parse(ExtractRuntimePackage(markerRuntimeScript)))
{
    Require(markerRuntime.RootElement.GetProperty("styles").GetProperty("light").GetProperty("home")
                .GetProperty("css").GetString()!.Contains("font-family:\"__CODEX_ADAPTER_JSON__\"", StringComparison.Ordinal),
        "A legal font family containing the adapter placeholder remains user data in the runtime package.");
}

Console.WriteLine("Rendering behavior and syntax checks passed; generated scripts were syntax-checked, and the caption runtime was executed only in an offline fake DOM/bridge harness.");
Console.WriteLine($"Offline caption colors and contrasts logged to {captionLogPath}.");

static string VerifyNativeCaptionPlan(ResolvedSkin skin, string name, string forLightSymbols, string forDarkSymbols)
{
    var plan = NativeSkinStyleBuilder.Build(skin, imageUrl: null).NativeCaption
        ?? throw new InvalidOperationException($"{name} should emit a native caption color plan.");
    Require(plan.ForLightSymbols == forLightSymbols, $"{name} emits the expected background for light native symbols.");
    Require(plan.ForDarkSymbols == forDarkSymbols, $"{name} emits the expected background for dark native symbols.");

    var lightSymbolsContrast = GetContrastRatio(forLightSymbols, "#FFFFFF");
    var darkSymbolsContrast = GetContrastRatio(forDarkSymbols, "#1F1F1F");
    Require(lightSymbolsContrast >= 4.5, $"{name}'s light native symbols retain at least 4.5:1 contrast.");
    Require(darkSymbolsContrast >= 4.5, $"{name}'s dark native symbols retain at least 4.5:1 contrast.");

    return $"{name}\tdark\t#FFFFFF\t{forLightSymbols}\t{lightSymbolsContrast:F2}:1\n{name}\tlight\t#1F1F1F\t{forDarkSymbols}\t{darkSymbolsContrast:F2}:1";
}

static double GetContrastRatio(string backgroundValue, string foregroundValue)
{
    if (!CssColorParser.TryParse(backgroundValue, out var background) || !CssColorParser.TryParse(foregroundValue, out var foreground))
    {
        throw new InvalidOperationException("Caption contrast values must be parseable CSS colors.");
    }

    static double Luminance(CssRgba color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.Red)) + (0.7152 * Linearize(color.Green)) + (0.0722 * Linearize(color.Blue));
    }

    var backgroundLuminance = Luminance(background);
    var foregroundLuminance = Luminance(foreground);
    return (Math.Max(backgroundLuminance, foregroundLuminance) + 0.05) /
           (Math.Min(backgroundLuminance, foregroundLuminance) + 0.05);
}

static double GetComposerOpacity(SkinPackage package, double userTransparency)
{
    using var runtime = JsonDocument.Parse(ExtractRuntimePackage(SkinRuntimeCompiler.BuildRuntimeScript(package, userTransparency)));
    return runtime.RootElement.GetProperty("styles").GetProperty("light").GetProperty("home")
        .GetProperty("fillOpacity").GetProperty("composer").GetProperty("default").GetDouble();
}

static string? GetImageKey(ResolvedBackground? background)
{
    if (background is not { ImagePath: { } imagePath }) return null;
    var bytes = background.ImageBytes ?? throw new InvalidDataException($"Missing test image bytes for {imagePath}.");
    return "sha256-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

static SkinPackage CreateCaptionColorFixture(string directory, string windowColor, string accentColor)
{
    Directory.CreateDirectory(directory);
    var definition = new JsonObject
    {
        ["formatVersion"] = 1,
        ["id"] = "caption-alpha-fixture",
        ["name"] = "Caption alpha fixture",
        ["version"] = "1.0.0",
        ["appearance"] = "light",
        ["tokens"] = new JsonObject
        {
            ["color.surface.window"] = windowColor,
            ["color.accent"] = accentColor
        },
        ["components"] = new JsonObject
        {
            ["window"] = new JsonObject { ["background"] = "{color.surface.window}" }
        }
    };
    File.WriteAllText(Path.Combine(directory, "skin.json"), definition.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return SkinPackageReader.ReadFolder(directory);
}

static SkinPackage CreateImageOnlyCaptionFixture(string directory, string sourceDirectory)
{
    Directory.CreateDirectory(directory);
    var assetDirectory = Path.Combine(directory, "assets");
    Directory.CreateDirectory(assetDirectory);
    File.Copy(
        Path.Combine(sourceDirectory, "assets", "ayanami-background.png"),
        Path.Combine(assetDirectory, "background.png"));
    var definition = new JsonObject
    {
        ["formatVersion"] = 1,
        ["id"] = "caption-image-only-fixture",
        ["name"] = "Caption image-only fixture",
        ["version"] = "1.0.0",
        ["appearance"] = "light",
        ["background"] = new JsonObject { ["image"] = "assets/background.png" }
    };
    File.WriteAllText(Path.Combine(directory, "skin.json"), definition.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return SkinPackageReader.ReadFolder(directory);
}

static string CreateTransparentAyanamiVariant(string sourceDirectory)
{
    var destination = Path.Combine(Path.GetTempPath(), "skin-manager-ayanami-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(destination);
    foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
        var destinationFile = Path.Combine(destination, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.Copy(sourceFile, destinationFile);
    }

    var skinFile = Path.Combine(destination, "skin.json");
    var skin = JsonNode.Parse(File.ReadAllText(skinFile)) ?? throw new InvalidDataException("Invalid Ayanami skin JSON.");
    skin["components"]!["content"]!["background"] = "rgba(0, 0, 0, 0)";
    File.WriteAllText(skinFile, skin.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return destination;
}

static SkinPackage CreateChatTextFixture(
    string directory,
    string? primary,
    string? secondary,
    string? assistantColor = null)
{
    Directory.CreateDirectory(directory);
    var tokens = new JsonObject();
    if (primary is not null) tokens["color.text.primary"] = primary;
    if (secondary is not null) tokens["color.text.secondary"] = secondary;

    var components = new JsonObject();
    if (assistantColor is not null)
    {
        components["message.assistant"] = new JsonObject { ["color"] = assistantColor };
    }

    var definition = new JsonObject
    {
        ["formatVersion"] = 1,
        ["id"] = "chat-text-fixture",
        ["name"] = "Chat text fixture",
        ["version"] = "1.0.0",
        ["appearance"] = "dark",
        ["tokens"] = tokens,
        ["components"] = components
    };
    File.WriteAllText(
        Path.Combine(directory, "skin.json"),
        definition.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        new System.Text.UTF8Encoding(false));
    return SkinPackageReader.ReadFolder(directory);
}

static string ExtractRuntimePackage(string source)
{
    const string startMarker = "const runtimePackage = ";
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    if (start < 0) throw new InvalidOperationException("Generated runtime payload marker was not found.");
    start += startMarker.Length;
    var end = source.IndexOf(";\n", start, StringComparison.Ordinal);
    if (end < 0) throw new InvalidOperationException("Generated runtime payload end marker was not found.");
    return source[start..end];
}

static void VerifyJavaScriptSyntax(string source, string label)
{
    var path = Path.Combine(Path.GetTempPath(), $"skin-manager-{Guid.NewGuid():N}.js");
    File.WriteAllText(path, source);
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--check");
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Node's syntax checker.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Generated {label} JavaScript has a syntax error: {standardOutput}{standardError}");
        }
    }
    finally
    {
        File.Delete(path);
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "SkinManager.sln")))
        {
            return directory.FullName;
        }
    }

    throw new DirectoryNotFoundException("Could not locate the repository root.");
}
