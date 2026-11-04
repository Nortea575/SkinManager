using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Buffers.Binary;
using SkinManager.Core;
using SkinManager.Rendering;

var testRoot = Path.Combine(Path.GetTempPath(), $"SkinManagerCore-{Guid.NewGuid():N}");
var testPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/NhEAAAAASUVORK5CYII=");
Directory.CreateDirectory(testRoot);
var passed = 0;

try
{
    var ayanamiDirectory = Path.Combine(AppContext.BaseDirectory, "skin", "ayanami");
    var sayaDirectory = Path.Combine(AppContext.BaseDirectory, "skin", "saya");
    var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "skin", "native");
    using var basePackageStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SkinManager.Tests.Resources.CodexBase-26.1027.3384.0.zip")
        ?? throw new InvalidOperationException("缺少测试程序集中的原生预览资源。");
    using var basePackageBytes = new MemoryStream();
    basePackageStream.CopyTo(basePackageBytes);
    var basePackage = BasePackageReader.ReadZipBytes(basePackageBytes.ToArray());
    var ayanami = SkinPackageReader.ReadFolder(ayanamiDirectory);
    var native = SkinPackageReader.ReadFolder(nativeDirectory);
    using var ayanamiZipStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SkinManager.Tests.Resources.Ayanami.zip")
        ?? throw new InvalidOperationException("缺少测试程序集中的 Ayanami 皮肤包。");
    using var ayanamiZipBytes = new MemoryStream();
    ayanamiZipStream.CopyTo(ayanamiZipBytes);
    var ayanamiZip = SkinPackageReader.ReadZipBytes(ayanamiZipBytes.ToArray());
    var ayanamiImagePath = Path.Combine(ayanamiDirectory, "assets", "ayanami-background.png");
    var ayanamiImageBytes = File.ReadAllBytes(ayanamiImagePath);
    const string originalAyanamiImageSha256 = "58F7282E2C6CA261E3413186C88FE30F847F793B928B000398D871AD8729E862";
    var saya = SkinPackageReader.ReadFolder(sayaDirectory);
    using var sayaZipStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SkinManager.Tests.Resources.Saya.zip")
        ?? throw new InvalidOperationException("缺少测试程序集中的 Saya 皮肤包。");
    using var sayaZipBytes = new MemoryStream();
    sayaZipStream.CopyTo(sayaZipBytes);
    var sayaZip = SkinPackageReader.ReadZipBytes(sayaZipBytes.ToArray());
    var sayaImagePath = Path.Combine(sayaDirectory, "assets", "saya-background.png");
    var sayaImageBytes = File.ReadAllBytes(sayaImagePath);
    const string originalSayaImageSha256 = "5454DA6C93F3F26E3732B4F2C86871363FF4FA68EFBE3676287B9B7ACAFDFECD";

    var requiredParts = new[] { "window", "content", "sidebar", "composer", "composer.editor", "composer.toolbar", "dialog" };
    Assert(requiredParts.All(basePackage.Parts.Contains), "原生基础包登记全部必需的自有部件");
    var baseHtml = Encoding.UTF8.GetString(basePackage.EntryBytes);
    Assert(baseHtml.Contains("data-skin-part=", StringComparison.Ordinal) && !baseHtml.Contains("data-ds-part", StringComparison.Ordinal),
        "原生基础HTML使用独立 data-skin-part 标记");
    Pass("原生基础包自有 parts 标记与 manifest 校验");

    Assert(ayanami.Id == "ayanami" && ayanami.Version == "1.0.3" && ayanamiZip.Version == "1.0.3" &&
           ayanami.Appearance == SkinAppearance.Light && ayanamiZip.Appearance == SkinAppearance.Light,
        "Ayanami 文件夹与 ZIP 元数据均声明 1.0.3 和浅色默认外观");
    Assert(ayanami.Definition.Author == "Ancimen" && ayanamiZip.Definition.Author == "Ancimen",
        "Ayanami 文件夹与 ZIP 均声明官方作者");
    Assert(ayanami.ImagePath == "assets/ayanami-background.png" && ayanami.ImageBytes is { Length: > 0 }, "Ayanami 背景路径与字节可读取");
    Assert(ReferenceEquals(ayanami.Files["assets/ayanami-background.png"], ayanami.ImageBytes),
        "SkinPackage 的图片属性与文件表共享同一资源缓冲区");
    var ayanamiLight = SkinResolver.Resolve(ayanami, SkinAppearance.Light, SkinScene.Home);
    var ayanamiDarkConversation = SkinResolver.Resolve(ayanami, SkinAppearance.Dark, SkinScene.Conversation);
    Assert(ReferenceEquals(ayanami.Files["assets/ayanami-background.png"], ayanamiLight.Background?.ImageBytes) &&
           ReferenceEquals(ayanami.Files["assets/ayanami-background.png"], ayanamiDarkConversation.Background?.ImageBytes),
        "Resolver 的浅色与深色背景均复用包内图片缓冲区");
    Assert(ayanamiLight.Tokens["material.fillOpacity"].Kind == SkinValueKind.Ratio &&
           ayanamiLight.Tokens["material.fillOpacity"].Number == 0.25,
        "Ayanami 75% 面板透明对应 25% 填充 alpha");
    Assert(ayanamiLight.Background?.ImageBytes is { Length: > 0 } && ayanamiLight.Background.Opacity == 1 && ayanamiLight.Background.OverlayOpacity == 0.08,
        "Ayanami 浅色背景显示图片并保留轻遮罩");
    Assert(ayanamiDarkConversation.Background?.Opacity == 0.55 && ayanamiDarkConversation.Background.OverlayOpacity == 0.70,
        "Ayanami 深色图像与遮罩透明度独立解析");
    Assert(ayanamiLight.Tokens["color.surface.window"].Text == "#f5f7fb" && ayanamiLight.Tokens["color.surface.sidebar"].Text == "#f9fbff" &&
           ayanamiLight.Tokens["color.surface.input"].Text == "#e7eef8" && ayanamiLight.Tokens["color.text.primary"].Text == "#202b3e" &&
           ayanamiLight.Tokens["color.text.secondary"].Text == "#61708b" && ayanamiLight.Tokens["color.border.default"].Text == "#c8d2e5" &&
           ayanamiLight.Tokens["color.accent"].Text == "#5875a7", "Ayanami 浅色令牌采用蓝灰配色");
    Assert(ayanamiDarkConversation.Tokens["color.surface.window"].Text == "#222733" &&
           ayanamiDarkConversation.Tokens["color.surface.sidebar"].Text == "#2c3240" &&
           ayanamiDarkConversation.Tokens["color.surface.input"].Text == "#3b4454" &&
           ayanamiDarkConversation.Tokens["color.text.primary"].Text == "#e6ecf7" &&
           ayanamiDarkConversation.Tokens["color.text.secondary"].Text == "#b0bdd3" &&
           ayanamiDarkConversation.Tokens["color.border.default"].Text == "#5b6a84" &&
           ayanamiDarkConversation.Tokens["color.accent"].Text == "#a0b7df", "Ayanami 深色令牌采用蓝灰配色");
    Assert(ayanamiDarkConversation.Components["composer"].States["focused"]["borderColor"].Text == "#a0b7df" &&
           ayanamiDarkConversation.Components["composer"].Properties["radius"].Number == 12 &&
           ayanamiDarkConversation.Components["content"].Properties["background"].Text == "none" &&
           !ayanami.Definition.Tokens.ContainsKey("font.family") && !ayanami.Definition.Components.ContainsKey("home.hero"),
        "Ayanami 输入框 focused 引用与圆角保留，内容层不重复覆盖背景图");
    var folderPackage = SkinPackageReader.ReadFolder(ayanamiDirectory);
    Assert(folderPackage.Id == "ayanami" && folderPackage.Files["assets/ayanami-background.png"].SequenceEqual(ayanami.Files["assets/ayanami-background.png"]),
        "文件夹导入保留皮肤图片资源");
    var sampleImageSha256 = Convert.ToHexString(SHA256.HashData(ayanamiImageBytes));
    var packagedImageBytes = ayanamiZip.Files["assets/ayanami-background.png"];
    var packagedImageSha256 = Convert.ToHexString(SHA256.HashData(packagedImageBytes));
    Assert(sampleImageSha256 == originalAyanamiImageSha256 && packagedImageSha256 == originalAyanamiImageSha256 &&
           ayanami.ImageBytes!.SequenceEqual(ayanamiImageBytes) && ayanamiImageBytes.SequenceEqual(packagedImageBytes),
        "Ayanami 样例与 ZIP 中的背景图 SHA-256 均与原图一致");
    AssertFileBytes(ayanami.Files, ayanamiZip.Files, "Ayanami ZIP 与文件夹包含相同文件字节");
    Pass("Ayanami 独立格式样例、变体、背景层、图片字节与 focused 状态");

    Assert(saya.Definition.FormatVersion == 1 && saya.Id == "saya" && saya.Name == "Saya" && saya.Version == "1.0.2" && sayaZip.Version == "1.0.2" &&
           saya.Appearance == SkinAppearance.Dark && sayaZip.Appearance == SkinAppearance.Dark,
        "Saya 文件夹与 ZIP 元数据使用 skin.json v1，并默认深色外观");
    Assert(saya.Definition.Author == "Ancimen" && sayaZip.Definition.Author == "Ancimen",
        "Saya 文件夹与 ZIP 均声明官方作者");
    var sayaDarkHome = SkinResolver.Resolve(saya, SkinAppearance.Dark, SkinScene.Home);
    var sayaDarkConversation = SkinResolver.Resolve(sayaZip, SkinAppearance.Dark, SkinScene.Conversation);
    var sayaLightHome = SkinResolver.Resolve(saya, SkinAppearance.Light, SkinScene.Home);
    Assert(sayaDarkHome.Tokens["material.fillOpacity"].Kind == SkinValueKind.Ratio &&
           sayaDarkHome.Tokens["material.fillOpacity"].Number == 0.4 &&
           sayaDarkConversation.Components["composer"].Properties["fillOpacity"].Number == 0.4,
        "Saya 输入面板填充 alpha 为 40%，对应 60% 透明度");
    Assert(sayaDarkHome.Background?.ImagePath == "assets/saya-background.png" && sayaDarkHome.Background.Opacity == 1 &&
           sayaDarkHome.Background.OverlayColor == "#0a131c" && sayaDarkHome.Background.OverlayOpacity == 0.35 &&
           sayaDarkHome.Tokens["color.surface.window"].Text == "#13202b" &&
           sayaDarkHome.Tokens["color.text.primary"].Text == "#e5efe9" &&
           sayaDarkHome.Tokens["color.accent"].Text == "#91c7b0" &&
           sayaDarkConversation.Components["composer"].States["focused"]["borderColor"].Text == "#91c7b0",
        "Saya 深色主题保留背景图、深蓝/青绿配色与 focused 强调描边");
    Assert(sayaLightHome.Tokens["color.surface.window"].Text == "#f1f5f2" &&
           sayaLightHome.Tokens["color.text.primary"].Text == "#233a37" &&
           sayaLightHome.Background?.OverlayOpacity == 0.55,
        "Saya 提供可手动选择的浅色变体");
    AssertFileBytes(saya.Files, sayaZip.Files, "Saya ZIP 与文件夹包含相同文件字节");
    var sayaFolderHash = Convert.ToHexString(SHA256.HashData(sayaImageBytes));
    var sayaPackageHash = Convert.ToHexString(SHA256.HashData(sayaZip.Files["assets/saya-background.png"]));
    Assert(sayaFolderHash == originalSayaImageSha256 && sayaPackageHash == originalSayaImageSha256 &&
           saya.ImageBytes!.SequenceEqual(sayaImageBytes),
        "Saya 样例与 ZIP 中的原图 SHA-256 保持一致");
    var sayaStore = new SkinStore(Path.Combine(testRoot, "saya-store"));
    sayaStore.Initialize();
    sayaStore.Save(saya, replaceExisting: false);
    var sayaExportPath = Path.Combine(testRoot, "saya-export.zip");
    sayaStore.ExportZip("saya", sayaExportPath);
    var sayaReimported = SkinPackageReader.ReadZipFile(sayaExportPath);
    AssertFileBytes(saya.Files, sayaReimported.Files, "Saya 自包含 ZIP 导出后可重新导入且所有文件字节不变");
    Assert(Convert.ToHexString(SHA256.HashData(sayaReimported.Files["assets/saya-background.png"])) == originalSayaImageSha256,
        "Saya 导出再导入保留原始背景图 SHA-256");
    Pass("Saya 默认深色、透明度、浅色变体与自包含 ZIP 往返");

    var empty = SkinPackageReader.ReadZipBytes(BuildZip(Metadata("empty", "空样例")));
    var emptyResolved = SkinResolver.Resolve(empty, SkinAppearance.Light, SkinScene.Home);
    Assert(empty.Appearance == SkinAppearance.System, "appearance 缺省为 system");
    Assert(empty.ImagePath is null && empty.ImageBytes is null && empty.FocusX is null && empty.FocusY is null, "无图样例不补图片焦点默认值");
    Assert(emptyResolved.Tokens.Count == 0 && emptyResolved.Components.Count == 0 && emptyResolved.Background is null && emptyResolved.Colors.Count == 0,
        "仅元数据皮肤保持空样式并沿用原生外观");
    Assert(native.Definition.FormatVersion == 1 && native.Version == "1.0.1" && native.Definition.Author == "Ancimen" &&
           native.Files.Count == 1 && native.Files.ContainsKey("skin.json"), "Native 官方样例声明作者且维持最小包结构");
    Pass("默认原生外观与无图最小包");

    var typed = SkinPackageReader.ReadZipBytes(BuildZip("""
    {
      "formatVersion": 1, "id": "typed", "name": "类型", "version": "1",
      "tokens": {
        "color.accent": "#12345680",
        "color.text.primary": "{color.accent}",
        "space.control": 8,
        "font.family": "Arial"
      },
      "components": {
        "composer": {
          "background": "{color.text.primary}",
          "padding": "{space.control}",
          "gap": "4px",
          "fontFamily": "{font.family}"
        }
      }
    }
    """));
    var typedResolved = SkinResolver.Resolve(typed, SkinAppearance.Dark, SkinScene.Home);
    Assert(typedResolved.Tokens["color.text.primary"].Text == "#12345680", "颜色令牌引用解引用并保留 alpha 原文");
    Assert(typedResolved.Colors["color.text.primary"] == "#12345680", "Colors 只映射有效颜色令牌");
    Assert(typedResolved.Components["composer"].Properties["padding"].Number == 8, "px 长度引用保留类型和值");
    Assert(typedResolved.Components["composer"].Properties["gap"].Number == 4, "px 长度接受显式 px 单位");
    Assert(typedResolved.Components["composer"].Properties["fontFamily"].Text == "Arial", "系统字体令牌引用可解析");
    Assert(CssColorParser.TryParse("rgba(17, 34, 51, 0.25)", out var parsed) && parsed.Alpha == 64, "复用 CSS 颜色解析器处理 alpha");
    Pass("颜色、px 长度、系统字体、令牌引用与 CssColorParser");

    ExpectInvalid("$.tokens.font.family", BuildZip("""
    {"formatVersion":1,"id":"wrongtype","name":"类型错误","version":"1","tokens":{"color.accent":"#123456","font.family":"{color.accent}"}}
    """), "跨类型令牌引用必须带具体路径失败");
    ExpectInvalid("$.tokens.color.accent", BuildZip("""
    {"formatVersion":1,"id":"cycle","name":"循环","version":"1","tokens":{"color.accent":"{color.text.primary}","color.text.primary":"{color.accent}"}}
    """), "循环令牌引用必须失败");
    ExpectInvalid("$.components.composer.gap", BuildZip("""
    {"formatVersion":1,"id":"missing","name":"缺引用","version":"1","components":{"composer":{"gap":"{space.control}"}}}
    """), "缺失令牌引用必须失败");
    Pass("引用类型不匹配、循环与缺失引用路径");

    ExpectInvalid("$.tokens.color.surface.wimdow", BuildZip("""
    {"formatVersion":1,"id":"typo","name":"字段错误","version":"1","tokens":{"color.surface.wimdow":"#fff"}}
    """), "未知令牌字段必须报路径");
    ExpectInvalid("$.components.composer.paddding", BuildZip("""
    {"formatVersion":1,"id":"typo2","name":"字段错误","version":"1","components":{"composer":{"paddding":8}}}
    """), "未知组件属性必须报路径");
    ExpectInvalid("$.formatVersion", BuildZip("""
    {"formatVersion":1,"formatVersion":1,"id":"duplicate","name":"重复字段","version":"1"}
    """), "重复 JSON 字段必须报路径");
    ExpectInvalid("$.appearance", BuildZip("""
    {"formatVersion":1,"id":"enum","name":"无效枚举","version":"1","appearance":"auto"}
    """), "无效枚举必须报路径");
    ExpectInvalid("$.tokens.color.accent", BuildZip("""
    {"formatVersion":1,"id":"nullvalue","name":"空值","version":"1","tokens":{"color.accent":null}}
    """), "显式 null 必须报路径");
    Pass("严格未知字段、重复字段、无效枚举与 null 拒绝");

    var disabled = SkinPackageReader.ReadZipBytes(BuildZip("""
    {"formatVersion":1,"id":"disabled","name":"关闭效果","version":"1","tokens":{"shadow.panel":"none"},"components":{"composer":{"background":"none","shadow":"none"}}}
    """));
    var disabledResolved = SkinResolver.Resolve(disabled, SkinAppearance.Light, SkinScene.Home);
    Assert(disabledResolved.Tokens["shadow.panel"].Text == "none" && disabledResolved.Components["composer"].Properties["background"].Text == "none" &&
           disabledResolved.Components["composer"].Properties["shadow"].Text == "none",
        "none 仅在 background/shadow 属性上关闭效果");
    Pass("none 关闭效果语义");

    Assert(SkinDefinitionCatalog.Tokens.Count == 27 && SkinDefinitionCatalog.Components.Count == 17 &&
           SkinDefinitionCatalog.ComponentProperties.Count == 22 && SkinDefinitionCatalog.States.Count == 5 &&
           SkinDefinitionCatalog.DefaultTokenEditorFields.Count() == 21 && SkinDefinitionCatalog.DefaultComponentEditorFields.Count() == 13,
        "单一 catalog 保留全部 token、组件、属性和状态，同时沿用制作器默认字段数量");
    Assert(SkinDefinitionCatalog.TryGetToken("material.fillOpacity", out var fillOpacityDefinition) &&
           fillOpacityDefinition.DefaultValue == "75" && fillOpacityDefinition.Unit == "%" &&
           fillOpacityDefinition.DisplayTransform == SkinFieldDisplayTransform.TransparencyPercent &&
           SkinDefinitionCatalog.TryGetToken("shadow.panel", out var panelShadowDefinition) &&
           panelShadowDefinition.EditorKind == SkinEditorFieldKind.Text,
        "catalog 保留透明度百分比换算和 shadow.panel 标量文本编辑能力");
    var catalogPackage = SkinPackageReader.ReadZipBytes(BuildZip("""
    {
      "formatVersion": 1, "id": "catalog-fields", "name": "定义表字段", "version": "1", "appearance": "light",
      "components": {"composer": {
        "background": "none", "color": "#010203", "fillOpacity": 0.25,
        "borderColor": "#111213", "borderWidth": 2,
        "borderTopColor": "#212223", "borderRightColor": "#313233", "borderBottomColor": "#414243", "borderLeftColor": "#515253",
        "borderTopWidth": 3, "borderRightWidth": 4, "borderBottomWidth": 5, "borderLeftWidth": 6,
        "radius": 7, "fontFamily": "Arial", "fontSize": 15, "fontWeight": 500, "lineHeight": 1.6,
        "padding": 8, "gap": 9, "blur": 4,
        "shadow": {"x": 1, "y": 2, "blur": 3, "spread": 0, "color": "#000000", "opacity": 0.5}
      }},
      "background": {"color": "none", "overlayColor": "none"},
      "variants": {"dark": {"background": {"color": "none", "overlayColor": "none"}}},
      "scenes": {"home": {"background": {"color": "none", "overlayColor": "none"}}}
    }
    """));
    var catalogResolved = SkinResolver.Resolve(catalogPackage, SkinAppearance.Dark, SkinScene.Home);
    var baseBackgroundResolved = SkinResolver.Resolve(catalogPackage, SkinAppearance.Light, SkinScene.Conversation);
    var variantBackgroundResolved = SkinResolver.Resolve(catalogPackage, SkinAppearance.Dark, SkinScene.Conversation);
    Assert(new[] { baseBackgroundResolved.Background, variantBackgroundResolved.Background, catalogResolved.Background }
               .All(background => background is { Color: "none", OverlayColor: "none" }) &&
           catalogResolved.Components["composer"].Properties.Count == 22,
        "catalog 所有组件属性均可解析，base、variant 和 scene 的背景 none 均保持兼容");
    var catalogCss = NativeSkinStyleBuilder.Build(catalogResolved, imageUrl: null).Css;
    Assert(SkinDefinitionCatalog.ComponentProperties
               .Where(item => item.CssProperty is not null)
               .All(item => catalogCss.Contains(item.CssProperty + ":", StringComparison.Ordinal)) &&
           catalogCss.Contains("background-color:transparent", StringComparison.Ordinal) &&
           catalogCss.Contains("backdrop-filter:blur(4px)", StringComparison.Ordinal) &&
           catalogCss.Contains("box-shadow:", StringComparison.Ordinal),
        "Rendering 按 catalog 的 Kind/CSS 元数据输出全部组件属性、none 底色、模糊和复合 shadow");
    Pass("单一字段 catalog、背景关闭兼容与 Rendering 组件属性衔接");

    var layerZip = BuildZip("""
    {
      "formatVersion": 1, "id": "layers", "name": "层叠", "version": "1",
      "tokens": {"color.accent":"#111111"},
      "components": {"composer":{"padding":12,"states":{"focused":{"borderColor":"#aa0000","borderWidth":1}}}},
      "background": {"image":"assets/test.png","opacity":0.6,"overlayColor":"#00000088","overlayOpacity":0.2},
      "variants": {"dark": {
        "tokens":{"color.accent":"#222222"},
        "components":{"composer":{"padding":20}},
        "background":{"fit":"contain"}
      }},
      "scenes": {"conversation": {
        "tokens":{"color.accent":"#333333"},
        "components":{"composer":{"states":{"focused":{"borderColor":"#00ff00"}}}},
        "background":{"overlayOpacity":0.8}
      }}
    }
    """ , testPng);
    var layers = SkinPackageReader.ReadZipBytes(layerZip);
    var layersHome = SkinResolver.Resolve(layers, SkinAppearance.Dark, SkinScene.Home, 0.35);
    var layersConversation = SkinResolver.Resolve(layers, SkinAppearance.Dark, SkinScene.Conversation, 0.35);
    Assert(layersHome.Tokens["color.accent"].Text == "#222222", "appearance 變體在 scene 前覆蓋 base");
    Assert(layersConversation.Tokens["color.accent"].Text == "#333333", "scene 在 appearance 後覆蓋同一令牌");
    Assert(layersConversation.Components["composer"].Properties["padding"].Number == 20, "變體元件屬性合併保留");
    Assert(layersConversation.Components["composer"].States["focused"]["borderColor"].Text == "#00ff00" &&
           layersConversation.Components["composer"].States["focused"]["borderWidth"].Number == 1,
        "scene state 局部覆蓋且保留原 state 其它属性");
    Assert(layersConversation.Background?.Fit == "contain" && layersConversation.Background.Opacity == 0.6 &&
           layersConversation.Background.OverlayOpacity == 0.8,
        "背景字段级合并并独立保留图像透明度与遮罩透明度");
    Assert(layersConversation.PanelOpacityOverride == 0.35, "面板透明度覆盖作为可选解析上下文传递");
    Pass("appearance/scene 顺序、局部合并与透明度覆盖");

    var draft = SkinDraft.FromPackage(ayanami);
    var undoManifest = (System.Text.Json.Nodes.JsonObject)draft.Manifest.DeepClone();
    var originalDraftImagePath = draft.Manifest["background"]!["image"]!.GetValue<string>();
    draft.SetBackgroundImage(testPng, "undo-proof.png");
    var newDraftImagePath = draft.Manifest["background"]!["image"]!.GetValue<string>();
    var draftPackage = draft.BuildPackage();
    var draftPreviewPackage = draft.BuildPackage();
    var newImageManifest = (System.Text.Json.Nodes.JsonObject)draft.Manifest.DeepClone();
    Assert(newDraftImagePath != originalDraftImagePath && newDraftImagePath.StartsWith("assets/undo-proof-", StringComparison.Ordinal),
        "新背景资源使用独立路径，保留 undo 快照引用的旧图");
    Assert(draftPackage.Definition.Variants.ContainsKey(SkinAppearance.Dark) &&
           draftPackage.Definition.Components["composer"].States.ContainsKey("focused") &&
           ReferenceEquals(draftPackage.Files[newDraftImagePath], draftPreviewPackage.Files[newDraftImagePath]) &&
           ReferenceEquals(draftPackage.Files[newDraftImagePath], draftPackage.ImageBytes),
        "Draft.BuildPackage 保留原有变体和状态，重复预览共享同一图像字节数组");
    draft.SetBackgroundImage(ayanami.Files[originalDraftImagePath], "ayanami-background.png");
    var returnedToOriginalPackage = draft.BuildPackage();
    Assert(returnedToOriginalPackage.ImagePath == originalDraftImagePath &&
           ReferenceEquals(returnedToOriginalPackage.ImageBytes, ayanami.Files[originalDraftImagePath]) &&
           !returnedToOriginalPackage.Files.ContainsKey(newDraftImagePath),
        "A→B→A 会复用原图资源，BuildPackage 不打包已脱离当前清单的新增 B 图");
    draft.RestoreManifest(newImageManifest);
    draft.PruneUnreferencedResources([undoManifest]);
    var restoredNewImagePackage = draft.BuildPackage();
    Assert(restoredNewImagePackage.ImagePath == newDraftImagePath &&
           restoredNewImagePackage.Files[newDraftImagePath].SequenceEqual(testPng),
        "Manifest undo 快照保留 B 图资源直到历史裁剪");
    draft.RestoreManifest(undoManifest);
    draft.PruneUnreferencedResources([]);
    var undoPackage = draft.BuildPackage();
    Assert(undoPackage.ImagePath == originalDraftImagePath &&
           ReferenceEquals(undoPackage.ImageBytes, ayanami.Files[originalDraftImagePath]) &&
           !undoPackage.Files.ContainsKey(newDraftImagePath),
        "恢复原图快照并裁剪 undo 历史后不再保留新增 B 图资源");
    var sceneDraftPackage = SkinDraft.FromPackage(layers).BuildPackage();
    Assert(sceneDraftPackage.Definition.Variants.ContainsKey(SkinAppearance.Dark) &&
           sceneDraftPackage.Definition.Scenes[SkinScene.Conversation].Components["composer"].States["focused"]["borderColor"].Text == "#00ff00" &&
           sceneDraftPackage.Files["assets/test.png"].SequenceEqual(testPng),
        "Draft 往返保留 scene、variant、state 与已有图片资源");
    var createdDraft = SkinDraft.CreateNew("draft-created", "新建草稿");
    Assert(createdDraft.BuildPackage() is { Version: "1.0.0", Appearance: SkinAppearance.System },
        "新建 Draft 使用 skin.json v1 与可解析的默认元数据");
    ExpectThrows<InvalidDataException>(() => SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
    {
        ["../skin.json"] = Encoding.UTF8.GetBytes(Metadata("unsafe", "非法路径"))
    }), "内存文件入口复用包内路径校验");
    Pass("内存资源所有权、SkinDraft 快照与无 ZIP 预览包");

    using (var nativeRuntime = ReadRuntimePackage(SkinRuntimeCompiler.BuildRuntimeScript(native)))
    {
        var nativeRoot = nativeRuntime.RootElement;
        Assert(!nativeRoot.GetProperty("enabled").GetBoolean() &&
               nativeRoot.GetProperty("styles").GetProperty("light").GetProperty("home").GetProperty("css").GetString() == string.Empty &&
               nativeRoot.GetProperty("styles").GetProperty("dark").GetProperty("conversation").GetProperty("css").GetString() == string.Empty,
            "空 Native 包编译为四组空原生样式且不启用 override");
    }

    var palette = SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
    {
        ["skin.json"] = Encoding.UTF8.GetBytes("""
        {
          "formatVersion": 1, "id": "palette", "name": "三色映射", "version": "1.0.0", "appearance": "light",
          "tokens": {
            "color.surface.window": "#f4f6fb", "color.surface.sidebar": "#e9edf5", "color.surface.input": "#e1e8f3",
            "color.text.primary": "#233146", "color.accent": "#5575a8"
          },
          "components": {
            "composer": { "background": "{color.surface.input}", "states": { "focused": { "borderColor": "{color.accent}" } } },
            "button.primary": { "color": "#123456" }
          },
          "variants": {
            "dark": { "tokens": { "color.surface.window": "#202735", "color.text.primary": "#e6ebf3", "color.accent": "#9ab3df" } }
          },
          "scenes": {
            "home": { "components": { "composer": { "states": { "focused": { "borderColor": "#57a0d5" } } } } },
            "conversation": { "tokens": { "color.accent": "#ad6842" } }
          }
        }
        """)
    });
    var paletteRuntimeScript = SkinRuntimeCompiler.BuildRuntimeScript(palette);
    VerifyJavaScriptSyntax(paletteRuntimeScript, "四组运行时编译脚本");
    VerifyJavaScriptSyntax(
        NativeSkinStyleBuilder.BuildApplyScript(NativeSkinStyleBuilder.Build(
            SkinResolver.Resolve(palette, SkinAppearance.Dark, SkinScene.Conversation), null)),
        "原生调色板应用脚本");
    using (var paletteRuntime = ReadRuntimePackage(paletteRuntimeScript))
    {
        var styles = paletteRuntime.RootElement.GetProperty("styles");
        var nativeCss = string.Join('\n', basePackage.Styles.Select(path => Encoding.UTF8.GetString(basePackage.Files[path])));
        var nativeVariables = Regex.Matches(nativeCss, @"--[a-zA-Z0-9_-]+")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (appearanceName, appearance) in new[]
        {
            ("light", SkinAppearance.Light),
            ("dark", SkinAppearance.Dark)
        })
        {
            foreach (var (sceneName, scene) in new[]
            {
                ("home", SkinScene.Home),
                ("conversation", SkinScene.Conversation)
            })
            {
                var resolved = SkinResolver.Resolve(palette, appearance, scene);
                var directCss = NativeSkinStyleBuilder.Build(resolved, null).Css;
                var runtimeCss = styles.GetProperty(appearanceName).GetProperty(sceneName).GetProperty("css").GetString();
                var unmappedVariables = Regex.Matches(directCss, @"--[a-zA-Z0-9_-]+")
                    .Select(match => match.Value)
                    .Where(name => !name.StartsWith("--skin-token-", StringComparison.Ordinal) && !nativeVariables.Contains(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                Assert(runtimeCss == directCss && runtimeCss!.Contains("--app-color-background-shell", StringComparison.Ordinal) &&
                       runtimeCss.Contains("--app-color-text-foreground", StringComparison.Ordinal) &&
                       runtimeCss.Contains("--codex-theme-accent-color", StringComparison.Ordinal) &&
                       runtimeCss.Contains("--app-color-background-button-primary-hover", StringComparison.Ordinal) &&
                       unmappedVariables.Length == 0,
                    $"{appearanceName}/{sceneName} runtime CSS 与预览一致，且映射变量均存在于原生 CSS");
            }
        }

        var lightHomeCss = styles.GetProperty("light").GetProperty("home").GetProperty("css").GetString()!;
        var lightConversationCss = styles.GetProperty("light").GetProperty("conversation").GetProperty("css").GetString()!;
        var darkHomeCss = styles.GetProperty("dark").GetProperty("home").GetProperty("css").GetString()!;
        var darkConversationCss = styles.GetProperty("dark").GetProperty("conversation").GetProperty("css").GetString()!;
        var contrastChecks = new[]
        {
            lightHomeCss.Contains("--color-text-primary-solid:#ffffff", StringComparison.Ordinal),
            darkHomeCss.Contains("--color-text-primary-solid:#000000", StringComparison.Ordinal),
            darkConversationCss.Contains("--app-color-text-on-accent:#000000", StringComparison.Ordinal),
            lightHomeCss.Contains("color:#123456 !important", StringComparison.Ordinal)
        };
        Assert(lightHomeCss.Contains("#f4f6fb", StringComparison.Ordinal) &&
               lightConversationCss.Contains("#ad6842", StringComparison.Ordinal) &&
               darkHomeCss.Contains("#202735", StringComparison.Ordinal) &&
               darkConversationCss.Contains("#ad6842", StringComparison.Ordinal) &&
               lightHomeCss.Contains("#57a0d5", StringComparison.Ordinal) &&
               contrastChecks.All(result => result),
            $"light/dark × home/conversation 遵守覆盖顺序，并为深浅 accent 选择高对比按钮文字且保留显式按钮文字覆盖（检查：{string.Join(',', contrastChecks)}）");
    }
    var alphaAccentPackage = SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
    {
        ["skin.json"] = Encoding.UTF8.GetBytes("""
        {"formatVersion":1,"id":"alpha-accent","name":"Alpha accent","version":"1","appearance":"dark","tokens":{"color.surface.window":"#202735","color.text.primary":"#e6ebf3","color.accent":"rgba(255,255,255,0.3)"}}
        """)
    });
    var alphaAccentCss = NativeSkinStyleBuilder.Build(
        SkinResolver.Resolve(alphaAccentPackage, SkinAppearance.Dark, SkinScene.Home), null).Css;
    Assert(alphaAccentCss.Contains("--color-text-primary-solid:#ffffff", StringComparison.Ordinal),
        "半透明 accent 先合成到 window surface，再选出对比度更高的按钮字色");
    Pass("Native 三色调色板与四组预览运行时样式一致");

    ExpectInvalid("$.background.image", BuildZip("""
    {"formatVersion":1,"id":"missingimage","name":"缺图","version":"1","background":{"image":"assets/missing.png"}}
    """), "缺失背景图必须按字段路径失败");
    ExpectInvalid("$.variants.dark.background.image", BuildZip("""
    {"formatVersion":1,"id":"variantimage","name":"变体缺图","version":"1","variants":{"dark":{"background":{"image":"assets/missing.png"}}}}
    """), "变体缺失背景图必须包含变体路径");
    Pass("缺失资源路径校验");

    var storePath = Path.Combine(testRoot, "skin");
    var store = new SkinStore(storePath);
    store.Initialize();
    Assert(store.DataDirectory == Path.GetFullPath(storePath) && store.SkinsDirectory == Path.GetFullPath(storePath), "数据根与唯一皮肤根均指向传入的 skin 目录");
    Assert(store.GetSkins().Count == 0 && !File.Exists(Path.Combine(storePath, ".seed-initialized")), "初始化只建目录，不 seed 或写 marker");
    var legacyStore = new SkinStore(Path.Combine(testRoot, "legacy-library", "Skins"));
    legacyStore.Initialize();
    legacyStore.Save(SkinPackageReader.ReadFolder(nativeDirectory), replaceExisting: false);
    Assert(store.GetSkins().Count == 0, "指定 skin 根之外的旧库不会作为备用来源扫描");
    store.Save(store.ReadFolder(nativeDirectory), replaceExisting: false);
    store.Save(store.ReadFolder(ayanamiDirectory), replaceExisting: false);
    Assert(store.GetSkins().Count == 2, "从真实 Native 与 Ayanami 文件夹导入后可读取两份声明");
    var lockedImagePath = Path.Combine(storePath, "ayanami", "assets", "ayanami-background.png");
    IReadOnlyList<SkinCatalogEntry> catalog;
    using (new FileStream(lockedImagePath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        catalog = store.GetCatalog();
    }

    var catalogAyanami = catalog.Single(entry => entry.Id == "ayanami");
    Assert(catalog.Count == 2 && catalogAyanami.Name == "Ayanami" && catalogAyanami.Version == "1.0.3" &&
           catalogAyanami.Appearance == SkinAppearance.Light && catalogAyanami.BackgroundImagePath == "assets/ayanami-background.png" &&
           catalogAyanami.FocusX == 0.5 && catalogAyanami.FocusY == 0.5,
        "GetCatalog 仅读 skin.json 元数据，即使大图文件被独占锁定也可读取目录卡片");
    var catalogImage = store.ReadCatalogImage(catalogAyanami);
    Assert(catalogImage is { Length: > 0 } && catalogImage.SequenceEqual(ayanami.Files[catalogAyanami.BackgroundImagePath!]),
        "ReadCatalogImage 只读所选皮肤的安全包内背景资源");
    Assert(store.LoadSkin("ayanami").ImageBytes is { Length: > 0 }, "LoadSkin 按 ID 完整读取并验证选中的皮肤包");
    ExpectThrows<InvalidDataException>(() => store.ReadCatalogImage(catalogAyanami with { BackgroundImagePath = "../secret.png" }),
        "缩略图入口拒绝越出皮肤目录的路径");
    Pass("目录元数据、按需缩略图与按 ID 加载");
    var scanPng = CreateLargeTestPng(testPng, 4 * 1024 * 1024);
    var scanMeasurements = MeasureCatalogScanning(Path.Combine(testRoot, "scan-comparison"), scanPng);
    Console.WriteLine($"Core 皮肤库扫描分配（20 个皮肤 × 4 MiB 图像）：GetCatalog {scanMeasurements.CatalogAllocated:N0} B；GetSkins {scanMeasurements.PackageAllocated:N0} B。该数值只计单次同步 Core 扫描，不代表整个程序 RAM 降幅。");
    Assert(scanMeasurements.CatalogCount == 20 && scanMeasurements.PackageCount == 20 &&
           scanMeasurements.CatalogAllocated * 8 < scanMeasurements.PackageAllocated,
        "目录 catalog 扫描分配显著低于完整包扫描，且两个入口发现相同数量皮肤");
    Pass("20 皮肤库目录扫描与完整包扫描分配对比");
    Assert(File.Exists(Path.Combine(storePath, "native", "skin.json")) &&
           File.ReadAllBytes(Path.Combine(storePath, "ayanami", "assets", "ayanami-background.png")).SequenceEqual(ayanami.Files["assets/ayanami-background.png"]),
        "导入将声明和图片资源物理写入唯一 skin 根");
    var exportPath = Path.Combine(testRoot, "ayanami-export.zip");
    store.ExportZip("ayanami", exportPath);
    var exported = SkinPackageReader.ReadZipFile(exportPath);
    AssertFileBytes(ayanami.Files, exported.Files, "ZIP 导出/导入往返保持所有文件字节");
    var nativeExportPath = Path.Combine(testRoot, "native-export.zip");
    store.ExportZip("native", nativeExportPath);
    AssertFileBytes(native.Files, SkinPackageReader.ReadZipFile(nativeExportPath).Files, "Native 声明作为普通皮肤包导出并可重新读取");
    var sentinelPath = Path.Combine(testRoot, "existing-export.zip");
    var sentinel = Encoding.UTF8.GetBytes("keep existing export");
    File.WriteAllBytes(sentinelPath, sentinel);
    ExpectThrows<IOException>(() => store.ExportZip("ayanami", sentinelPath), "默认导出拒绝覆盖既有文件");
    Assert(File.ReadAllBytes(sentinelPath).SequenceEqual(sentinel), "拒绝覆盖时既有导出文件保持原样");
    store.ExportZip("ayanami", sentinelPath, overwriteExisting: true);
    AssertFileBytes(ayanami.Files, SkinPackageReader.ReadZipFile(sentinelPath).Files, "确认覆盖时完整写入新 ZIP");

    var upgradedNativeZip = BuildZip(Metadata("native", "原生更新", "2.0.0"));
    var upgradedNative = SkinPackageReader.ReadZipBytes(upgradedNativeZip);
    store.Save(upgradedNative, replaceExisting: true);
    Assert(store.GetSkins().Single(package => package.Id == "native").Version == "2.0.0", "replace 原子替换为新包");
    ExpectThrows<DuplicateSkinException>(() => store.Save(native, replaceExisting: false), "未允许覆盖时拒绝同 ID 包");
    Assert(store.GetSkins().Single(package => package.Id == "native").Version == "2.0.0", "重复导入失败不损坏旧包");
    var corruptedReplacement = SkinPackageReader.ReadZipBytes(BuildZip(Metadata("native", "原生更新", "2.0.0")));
    corruptedReplacement.Files["skin.json"][0] = 0xff;
    ExpectThrows<InvalidDataException>(() => store.Save(corruptedReplacement, replaceExisting: true), "无效 replacement 在提交前失败");
    Assert(store.GetSkins().Single(package => package.Id == "native").Version == "2.0.0", "无效 replacement 失败不损坏旧包");

    store.Delete("native");
    Assert(!Directory.Exists(Path.Combine(storePath, "native")), "删除移除对应的物理皮肤目录");
    var restarted = new SkinStore(storePath);
    restarted.Initialize();
    Assert(restarted.GetSkins().Count == 1 && restarted.GetSkins()[0].Id == "ayanami", "删除后重启不会从样例、旧库或 marker 自动恢复");
    Pass("唯一 skin 根、物理导入、自包含导出、replace、delete 与 restart");

    Console.WriteLine($"Core tests passed: {passed}");
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

void Pass(string name)
{
    passed++;
    Console.WriteLine($"PASS {name}");
}

void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"FAIL {message}");
    }
}

void ExpectInvalid(string expectedPath, byte[] zipBytes, string message)
{
    try
    {
        _ = SkinPackageReader.ReadZipBytes(zipBytes);
    }
    catch (InvalidDataException exception) when (exception.Message.Contains(expectedPath, StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException($"FAIL {message}: 未收到包含“{expectedPath}”的 InvalidDataException。");
}

void ExpectThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"FAIL {message}: 未收到 {typeof(TException).Name}。");
}

void AssertFileBytes(IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual, string message)
{
    Assert(expected.Count == actual.Count, $"{message}（文件数量）");
    foreach (var pair in expected)
    {
        Assert(actual.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes), $"{message}（{pair.Key}）");
    }
}

static byte[] BuildZip(string json, byte[]? image = null)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        var manifest = archive.CreateEntry("skin.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
        {
            writer.Write(json);
        }

        if (image is not null)
        {
            var imageEntry = archive.CreateEntry("assets/test.png", CompressionLevel.Optimal);
            using var imageStream = imageEntry.Open();
            imageStream.Write(image);
        }
    }

    return output.ToArray();
}

static string Metadata(string id, string name, string version = "1.0.0") =>
    $$"""{"formatVersion":1,"id":"{{id}}","name":"{{name}}","version":"{{version}}","appearance":"system"}""";

static JsonDocument ReadRuntimePackage(string script)
{
    const string prefix = "const runtimePackage = ";
    var start = script.IndexOf(prefix, StringComparison.Ordinal);
    var packageStart = start < 0 ? -1 : start + prefix.Length;
    var end = packageStart < 0 ? -1 : script.IndexOf(";\n", packageStart, StringComparison.Ordinal);
    if (start < 0 || end < start + prefix.Length)
    {
        throw new InvalidDataException("运行时脚本缺少可解析的 runtimePackage JSON。");
    }

    return JsonDocument.Parse(script.AsMemory(packageStart, end - packageStart));
}

static (int CatalogCount, int PackageCount, long CatalogAllocated, long PackageAllocated) MeasureCatalogScanning(string directory, byte[] imageBytes)
{
    var skinsRoot = Path.Combine(directory, "skins");
    var store = new SkinStore(skinsRoot);
    store.Initialize();
    for (var index = 0; index < 20; index++)
    {
        var id = $"scan-{index:D2}";
        var skinDirectory = Path.Combine(skinsRoot, id);
        var assetsDirectory = Path.Combine(skinDirectory, "assets");
        Directory.CreateDirectory(assetsDirectory);
        var manifest = JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            id,
            name = $"扫描样例 {index:D2}",
            version = "1.0.0",
            appearance = "light",
            background = new { image = "assets/background.png" }
        });
        File.WriteAllText(Path.Combine(skinDirectory, "skin.json"), manifest, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(assetsDirectory, "background.png"), imageBytes);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var catalogStart = GC.GetAllocatedBytesForCurrentThread();
    var catalogCount = store.GetCatalog().Count;
    var catalogAllocated = GC.GetAllocatedBytesForCurrentThread() - catalogStart;

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var packageStart = GC.GetAllocatedBytesForCurrentThread();
    var packageCount = store.GetSkins().Count;
    var packageAllocated = GC.GetAllocatedBytesForCurrentThread() - packageStart;
    return (catalogCount, packageCount, catalogAllocated, packageAllocated);
}

static byte[] CreateLargeTestPng(byte[] template, int dataLength)
{
    if (template.Length < 45 || dataLength < 4)
    {
        throw new ArgumentOutOfRangeException(nameof(dataLength));
    }

    var iendOffset = 8;
    while (iendOffset + 12 <= template.Length)
    {
        var chunkLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(iendOffset, 4)));
        if (template.AsSpan(iendOffset + 4, 4).SequenceEqual("IEND"u8))
        {
            break;
        }

        iendOffset = checked(iendOffset + chunkLength + 12);
    }

    if (iendOffset + 12 != template.Length || !template.AsSpan(iendOffset + 4, 4).SequenceEqual("IEND"u8))
    {
        throw new InvalidDataException("大型扫描夹具所需的 PNG 模板缺少 IEND。");
    }

    var image = new byte[checked(template.Length + dataLength + 12)];
    template.AsSpan(0, iendOffset).CopyTo(image);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(iendOffset, 4), checked((uint)dataLength));
    "tEXt"u8.CopyTo(image.AsSpan(iendOffset + 4, 4));
    image[iendOffset + 8] = (byte)'x';
    image[iendOffset + 9] = 0;
    Array.Fill(image, (byte)'A', iendOffset + 10, dataLength - 2);
    var crc = ComputePngCrc32(image.AsSpan(iendOffset + 4, dataLength + 4));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(iendOffset + 8 + dataLength, 4), crc);
    template.AsSpan(iendOffset).CopyTo(image.AsSpan(iendOffset + dataLength + 12));
    return image;
}

static uint ComputePngCrc32(ReadOnlySpan<byte> bytes)
{
    var crc = uint.MaxValue;
    foreach (var value in bytes)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
    }

    return ~crc;
}

static void VerifyJavaScriptSyntax(string script, string label)
{
    var path = Path.Combine(Path.GetTempPath(), $"SkinManager-{Guid.NewGuid():N}.js");
    File.WriteAllText(path, script, new UTF8Encoding(false));
    try
    {
        using var process = Process.Start(new ProcessStartInfo("node")
        {
            ArgumentList = { "--check", path },
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("无法启动 Node.js 语法检查进程。");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = process.StandardError.ReadToEnd();
            throw new InvalidDataException($"{label} JavaScript 语法错误：{output}");
        }
    }
    finally
    {
        File.Delete(path);
    }
}

