using System.Text;
using SkinManager.Core;

internal static class CreationWorkflowTests
{
    public static int Run()
    {
        var passed = 0;
        VerifyNewThemesResolve();
        Pass("新建浅色与深色主题可解析，背景颜色引用主色");
        VerifyLightToDarkOpacityDefaults();
        Pass("浅色转深色时内置填充与遮罩透明度联动");
        VerifyDarkToLightOpacityDefaults();
        Pass("深色转浅色时内置填充与遮罩透明度联动");
        VerifyAppearanceChangePreservesIndependentEdits();
        Pass("切换外观只联动内置主色并保留独立编辑");
        VerifyCopyIsDetachedAndComplete();
        Pass("复制保留第三方作者与资源，并与原包隔离");
        VerifyExportImportRoundTrip();
        Pass("新建主题保存、导出、导入后数据与效果一致");
        return passed;

        void Pass(string message)
        {
            passed++;
            Console.WriteLine($"PASS {message}");
        }
    }

    private static void VerifyNewThemesResolve()
    {
        foreach (var appearance in new[] { SkinAppearance.Light, SkinAppearance.Dark })
        {
            var draft = SkinCreation.Create($"create-{appearance.ToString().ToLowerInvariant()}", "基础主题", appearance);
            var package = draft.BuildPackage();
            var resolved = SkinResolver.Resolve(package, appearance, SkinScene.Home);
            var expectedOverlayOpacity = SkinCreation.GetDefaultOverlayOpacity(appearance);
            var expectedTokens = new[]
            {
                "color.surface.window", "color.text.primary", "color.accent",
                "material.fillOpacity", "radius.control", "radius.panel"
            };

            Assert(package.Appearance == appearance, "清单记录所选外观");
            Assert(package.Definition.Tokens.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedTokens.Order(StringComparer.Ordinal)),
                "新主题只保存基础令牌");
            Assert(resolved.Colors.TryGetValue("color.surface.window", out var windowColor) &&
                   resolved.Background?.Color == windowColor && resolved.Background.OverlayColor == windowColor &&
                   resolved.Background.OverlayOpacity == expectedOverlayOpacity,
                "背景底色和遮罩颜色引用窗口表面令牌");
            Assert(resolved.Colors.ContainsKey("color.text.primary") && resolved.Colors.ContainsKey("color.accent"),
                "基础文字与强调色已解析");
            Assert(ContrastRatio(resolved.Colors["color.text.primary"], windowColor!) >= 4.5 &&
                   ContrastRatio(resolved.Colors["color.accent"], windowColor!) >= 4.5,
                $"{appearance} 内置主文字与强调色对窗口背景的对比度不低于 4.5:1");
        }
    }

    private static void VerifyAppearanceChangePreservesIndependentEdits()
    {
        var package = CreateCustomPackage();
        var draft = SkinDraft.FromPackage(package);
        var originalComponentBackground = draft.Manifest["components"]!["button.primary"]!["background"]!.GetValue<string>();
        var originalVariant = draft.Manifest["variants"]!.ToJsonString();
        var originalScene = draft.Manifest["scenes"]!.ToJsonString();

        var changed = SkinCreation.ChangeAppearance(draft, SkinAppearance.Dark);
        var changedPackage = changed.BuildPackage();
        var resolved = SkinResolver.Resolve(changedPackage, SkinAppearance.Dark, SkinScene.Conversation);

        Assert(ReferenceEquals(changed, draft) && changedPackage.Appearance == SkinAppearance.Dark,
            "切换原位更新并记录深色外观");
        Assert(changedPackage.Definition.Tokens["color.surface.window"].Text == "#202735" &&
               changedPackage.Definition.Tokens["color.text.primary"].Text == "#123456" &&
               changedPackage.Definition.Tokens["color.accent"].Text == "#2468AC",
            "仅仍等于旧内置色的窗口背景跟随新配色");
        Assert(changedPackage.Definition.Tokens["color.surface.sidebar"].Text == "#334455" &&
               changedPackage.Definition.Tokens["material.fillOpacity"].Number == 0.53,
            "额外令牌与独立材质设置保留");
        Assert(changed.Manifest["background"]!["overlayOpacity"]!.GetValue<double>() == 0.61,
            "独立遮罩透明度保留");
        Assert(changed.Manifest["components"]!["button.primary"]!["background"]!.GetValue<string>() == originalComponentBackground &&
               changed.Manifest["variants"]!.ToJsonString() == originalVariant &&
               changed.Manifest["scenes"]!.ToJsonString() == originalScene &&
               resolved.Colors["color.accent"] == "#778899",
            "组件、变体、场景及场景自定义强调色保留");
    }

    private static void VerifyLightToDarkOpacityDefaults()
    {
        var draft = SkinCreation.Create("opacity-light-dark", "Opacity", SkinAppearance.Light);
        var changed = SkinCreation.ChangeAppearance(draft, SkinAppearance.Dark).BuildPackage();
        Assert(changed.Definition.Tokens["material.fillOpacity"].Number == 0.40 &&
               SkinResolver.Resolve(changed, SkinAppearance.Dark, SkinScene.Home).Background?.OverlayOpacity == 0.35,
            "浅色默认填充与遮罩透明度切换为深色默认值");
    }

    private static void VerifyDarkToLightOpacityDefaults()
    {
        var draft = SkinCreation.Create("opacity-dark-light", "Opacity", SkinAppearance.Dark);
        var changed = SkinCreation.ChangeAppearance(draft, SkinAppearance.Light).BuildPackage();
        Assert(changed.Definition.Tokens["material.fillOpacity"].Number == 0.28 &&
               SkinResolver.Resolve(changed, SkinAppearance.Light, SkinScene.Home).Background?.OverlayOpacity == 0.08,
            "深色默认填充与遮罩透明度切换为浅色默认值");
    }

    private static void VerifyCopyIsDetachedAndComplete()
    {
        var package = CreateCustomPackage();
        var sourceResource = package.Files["assets/reference.png"];
        var copy = SkinCreation.CreateCopy(package, "external-copy", "External copy");
        var copyPackage = copy.BuildPackage();

        Assert(copyPackage.Id == "external-copy" && copyPackage.Name == "External copy" &&
               copyPackage.Definition.Author == "External Artist",
            "副本仅替换身份字段并保留第三方作者");
        Assert(copyPackage.Definition.Components.ContainsKey("button.primary") &&
               copyPackage.Definition.Variants.ContainsKey(SkinAppearance.Dark) &&
               copyPackage.Definition.Scenes.ContainsKey(SkinScene.Conversation) &&
               copyPackage.Files["assets/reference.png"].SequenceEqual(sourceResource),
            "副本保留组件、变体、场景与资源内容");
        Assert(!ReferenceEquals(copyPackage.Files["assets/reference.png"], sourceResource),
            "副本资源字节与原包相互独立");

        copyPackage.Files["assets/reference.png"][0] ^= 0xff;
        Assert(sourceResource[0] != copyPackage.Files["assets/reference.png"][0], "修改副本资源不影响原包");
    }

    private static void VerifyExportImportRoundTrip()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"SkinCreation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var original = SkinCreation.Create("creation-roundtrip", "Round trip", SkinAppearance.Light).BuildPackage();
            var store = new SkinStore(Path.Combine(tempDirectory, "skins"));
            store.Initialize();
            store.Save(original, replaceExisting: false);
            var zipPath = Path.Combine(tempDirectory, "creation-roundtrip.zip");
            store.ExportZip(original.Id, zipPath);
            var imported = SkinPackageReader.ReadZipFile(zipPath);

            Assert(original.Files.Count == imported.Files.Count && original.Files.All(pair =>
                    imported.Files.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes)),
                "保存、导出与导入完整保留文件");
            foreach (var appearance in new[] { SkinAppearance.Light, SkinAppearance.Dark })
            {
                var before = SkinResolver.Resolve(original, appearance, SkinScene.Home);
                var after = SkinResolver.Resolve(imported, appearance, SkinScene.Home);
                Assert(before.Colors.OrderBy(pair => pair.Key).SequenceEqual(after.Colors.OrderBy(pair => pair.Key)) &&
                       before.Background?.Color == after.Background?.Color &&
                       before.Background?.OverlayColor == after.Background?.OverlayColor &&
                       before.Background?.OverlayOpacity == after.Background?.OverlayOpacity,
                    $"{appearance} 导入后的解析效果与导出前一致");
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static SkinPackage CreateCustomPackage()
    {
        const string manifest = """
        {
          "formatVersion": 1,
          "id": "external-source",
          "name": "External source",
          "version": "2.0.0",
          "author": "External Artist",
          "appearance": "light",
          "tokens": {
            "color.surface.window": "#F7F9FC",
            "color.text.primary": "#123456",
            "color.accent": "#2468AC",
            "color.surface.sidebar": "#334455",
            "material.fillOpacity": 0.53
          },
          "components": {
            "button.primary": { "background": "#112233" }
          },
          "variants": {
            "dark": { "tokens": { "color.surface.sidebar": "#445566" } }
          },
          "scenes": {
            "conversation": { "tokens": { "color.accent": "#778899" } }
          },
          "background": {
            "color": "{color.surface.window}",
            "image": "assets/reference.png",
            "overlayOpacity": 0.61
          }
        }
        """;
        return SkinPackageReader.ReadFiles(new Dictionary<string, byte[]>
        {
            ["skin.json"] = Encoding.UTF8.GetBytes(manifest),
            ["assets/reference.png"] = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/NhEAAAAASUVORK5CYII=")
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"FAIL Creation workflow: {message}");
        }
    }

    private static double ContrastRatio(string foreground, string background)
    {
        var foregroundLuminance = RelativeLuminance(foreground);
        var backgroundLuminance = RelativeLuminance(background);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
               (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    }

    private static double RelativeLuminance(string value)
    {
        if (!CssColorParser.TryParse(value, out var color))
        {
            throw new InvalidOperationException($"测试无法解析颜色“{value}”。");
        }

        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.Red) + 0.7152 * Linearize(color.Green) + 0.0722 * Linearize(color.Blue);
    }
}
