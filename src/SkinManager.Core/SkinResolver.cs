namespace SkinManager.Core;

public static class SkinResolver
{
    private sealed record SourcedValue(SkinValue Value, string Path);

    private sealed class MergedComponent
    {
        public Dictionary<string, SourcedValue> Properties { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, SourcedValue>> States { get; } = new(StringComparer.Ordinal);
    }

    public static ResolvedSkin Resolve(
        SkinPackage package,
        SkinAppearance actualAppearance,
        SkinScene scene,
        double? panelOpacityOverride = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (actualAppearance is not (SkinAppearance.Light or SkinAppearance.Dark))
        {
            throw Invalid("$.appearance", "解析时必须指定实际 light 或 dark 外观。");
        }

        if (scene is not (SkinScene.Home or SkinScene.Conversation))
        {
            throw Invalid("$.scene", "只能是 home 或 conversation。");
        }

        if (panelOpacityOverride.HasValue && (!double.IsFinite(panelOpacityOverride.Value) || panelOpacityOverride.Value is < 0 or > 1))
        {
            throw Invalid("$.panelOpacityOverride", "必须是 0 到 1 之间的有限数字。");
        }

        var tokens = new Dictionary<string, SourcedValue>(StringComparer.Ordinal);
        var components = new Dictionary<string, MergedComponent>(StringComparer.Ordinal);
        var background = new Dictionary<string, SourcedValue>(StringComparer.Ordinal);
        ApplyBase(package.Definition, tokens, components, background);

        if (package.Definition.Variants.TryGetValue(actualAppearance, out var variant))
        {
            ApplyPatch(variant, $"$.variants.{actualAppearance.ToString().ToLowerInvariant()}", tokens, components, background);
        }

        if (package.Definition.Scenes.TryGetValue(scene, out var scenePatch))
        {
            ApplyPatch(scenePatch, $"$.scenes.{scene.ToString().ToLowerInvariant()}", tokens, components, background);
        }

        var resolvedTokens = ResolveTokens(tokens);
        var resolvedComponents = ResolveComponents(components, tokens);
        var resolvedBackground = ResolveBackground(package, background, tokens);
        return new ResolvedSkin(actualAppearance, scene, panelOpacityOverride, resolvedTokens, resolvedComponents, resolvedBackground);
    }

    private static void ApplyBase(
        SkinDefinition definition,
        IDictionary<string, SourcedValue> tokens,
        IDictionary<string, MergedComponent> components,
        IDictionary<string, SourcedValue> background)
    {
        foreach (var pair in definition.Tokens)
        {
            tokens[pair.Key] = new SourcedValue(pair.Value, $"$.tokens.{pair.Key}");
        }

        foreach (var pair in definition.Components)
        {
            ApplyComponent(pair.Key, pair.Value, $"$.components.{pair.Key}", components);
        }

        if (definition.Background is not null)
        {
            ApplyBackground(definition.Background, "$.background", background);
        }
    }

    private static void ApplyPatch(
        SkinPatch patch,
        string path,
        IDictionary<string, SourcedValue> tokens,
        IDictionary<string, MergedComponent> components,
        IDictionary<string, SourcedValue> background)
    {
        foreach (var pair in patch.Tokens)
        {
            tokens[pair.Key] = new SourcedValue(pair.Value, $"{path}.tokens.{pair.Key}");
        }

        foreach (var pair in patch.Components)
        {
            ApplyComponent(pair.Key, pair.Value, $"{path}.components.{pair.Key}", components);
        }

        if (patch.Background is not null)
        {
            ApplyBackground(patch.Background, $"{path}.background", background);
        }
    }

    private static void ApplyComponent(string id, ComponentStyle source, string path, IDictionary<string, MergedComponent> components)
    {
        if (!components.TryGetValue(id, out var target))
        {
            target = new MergedComponent();
            components.Add(id, target);
        }

        foreach (var pair in source.Properties)
        {
            target.Properties[pair.Key] = new SourcedValue(pair.Value, $"{path}.{pair.Key}");
        }

        foreach (var state in source.States)
        {
            if (!target.States.TryGetValue(state.Key, out var stateValues))
            {
                stateValues = new Dictionary<string, SourcedValue>(StringComparer.Ordinal);
                target.States.Add(state.Key, stateValues);
            }

            foreach (var pair in state.Value)
            {
                stateValues[pair.Key] = new SourcedValue(pair.Value, $"{path}.states.{state.Key}.{pair.Key}");
            }
        }
    }

    private static void ApplyBackground(BackgroundDefinition source, string path, IDictionary<string, SourcedValue> target)
    {
        foreach (var pair in source.Properties)
        {
            target[pair.Key] = new SourcedValue(pair.Value, $"{path}.{pair.Key}");
        }
    }

    private static Dictionary<string, SkinValue> ResolveTokens(IReadOnlyDictionary<string, SourcedValue> source)
    {
        var result = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
        var stack = new List<string>();
        foreach (var token in source.Keys.Order(StringComparer.Ordinal))
        {
            result[token] = ResolveToken(token, source, stack);
        }

        return result;
    }

    private static SkinValue ResolveToken(string token, IReadOnlyDictionary<string, SourcedValue> source, List<string> stack)
    {
        if (!SkinDefinitionCatalog.TryGetToken(token, out var tokenDefinition))
        {
            throw Invalid("$.tokens." + token, "未知令牌。");
        }
        var expectedKind = tokenDefinition.Kind;

        if (!source.TryGetValue(token, out var raw))
        {
            throw InvalidFormat(stack.Count > 0 ? "$.tokens." + stack[^1] : "$.tokens", "引用了未定义令牌“{0}”。", token);
        }

        var cycleStart = stack.IndexOf(token);
        if (cycleStart >= 0)
        {
            var chain = string.Join(" → ", stack.Skip(cycleStart).Append(token));
            throw InvalidFormat(raw.Path, "令牌引用形成循环：{0}。", chain);
        }

        stack.Add(token);
        try
        {
            return ResolveValue(raw.Value, expectedKind, raw.Path, source, stack);
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static SkinValue ResolveValue(
        SkinValue value,
        SkinValueKind expectedKind,
        string path,
        IReadOnlyDictionary<string, SourcedValue> tokens,
        List<string> stack)
    {
        if (value.Kind == SkinValueKind.TokenReference)
        {
            var reference = value.Text!;
            if (!SkinDefinitionCatalog.TryGetToken(reference, out var referencedToken))
            {
                throw InvalidFormat(path, "引用了不存在或未知的令牌“{0}”。", reference);
            }
            var referencedKind = referencedToken.Kind;

            if (referencedKind != expectedKind)
            {
                throw InvalidFormat(path, "令牌“{0}”类型为 {1}，此处需要 {2}。",
                    reference, Describe(referencedKind), Describe(expectedKind));
            }

            if (!tokens.ContainsKey(reference))
            {
                throw InvalidFormat(path, "引用了未定义令牌“{0}”。", reference);
            }

            return ResolveToken(reference, tokens, stack);
        }

        if (value.Kind != expectedKind)
        {
            throw InvalidFormat(path, "值类型为 {0}，此处需要 {1}。", Describe(value.Kind), Describe(expectedKind));
        }

        if (value.Text == "none")
        {
            return value;
        }

        return value;
    }

    private static Dictionary<string, ResolvedComponentStyle> ResolveComponents(
        IReadOnlyDictionary<string, MergedComponent> components,
        IReadOnlyDictionary<string, SourcedValue> tokens)
    {
        var result = new Dictionary<string, ResolvedComponentStyle>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            var properties = ResolveStyleProperties(component.Value.Properties, tokens);
            var states = component.Value.States.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, SkinValue>)ResolveStyleProperties(pair.Value, tokens),
                StringComparer.Ordinal);
            result.Add(component.Key, new ResolvedComponentStyle(properties, states));
        }

        return result;
    }

    private static Dictionary<string, SkinValue> ResolveStyleProperties(
        IReadOnlyDictionary<string, SourcedValue> source,
        IReadOnlyDictionary<string, SourcedValue> tokens)
    {
        var result = new Dictionary<string, SkinValue>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (!SkinDefinitionCatalog.TryGetComponentProperty(pair.Key, out var propertyDefinition))
            {
                throw Invalid(pair.Value.Path, "未知组件样式属性。");
            }
            var expectedKind = propertyDefinition.Kind;

            result[pair.Key] = ResolveValue(pair.Value.Value, expectedKind, pair.Value.Path, tokens, []);
        }

        return result;
    }

    private static ResolvedBackground? ResolveBackground(
        SkinPackage package,
        IReadOnlyDictionary<string, SourcedValue> source,
        IReadOnlyDictionary<string, SourcedValue> tokens)
    {
        if (source.Count == 0)
        {
            return null;
        }

        string? color = null;
        string? imagePath = null;
        string? fit = null;
        double? focusX = null;
        double? focusY = null;
        double? opacity = null;
        string? overlayColor = null;
        double? overlayOpacity = null;
        foreach (var pair in source)
        {
            if (!SkinDefinitionCatalog.TryGetBackgroundProperty(pair.Key, out var definition))
            {
                throw Invalid(pair.Value.Path, "未知背景属性。");
            }

            var expected = definition.Kind;

            var value = expected == SkinValueKind.String
                ? pair.Value.Value
                : ResolveValue(pair.Value.Value, expected, pair.Value.Path, tokens, []);
            switch (pair.Key)
            {
                case "color": color = value.Text; break;
                case "image": imagePath = value.Text; break;
                case "fit": fit = value.Text; break;
                case "focusX": focusX = value.Number; break;
                case "focusY": focusY = value.Number; break;
                case "opacity": opacity = value.Number; break;
                case "overlayColor": overlayColor = value.Text; break;
                case "overlayOpacity": overlayOpacity = value.Number; break;
            }
        }

        byte[]? imageBytes = null;
        if (imagePath is not null)
        {
            if (!package.Files.TryGetValue(imagePath, out var bytes))
            {
                throw InvalidFormat(source["image"].Path, "皮肤包缺少背景图片“{0}”。", imagePath);
            }

            imageBytes = bytes;
        }

        return new ResolvedBackground(color, imagePath, imageBytes, fit, focusX, focusY, opacity, overlayColor, overlayOpacity);
    }

    private static string Describe(SkinValueKind kind) => kind switch
    {
        SkinValueKind.Color => AppLocalization.Get("颜色"),
        SkinValueKind.Length => AppLocalization.Get("px 长度"),
        SkinValueKind.Number => AppLocalization.Get("数字"),
        SkinValueKind.Ratio => AppLocalization.Get("比例"),
        SkinValueKind.FontFamily => AppLocalization.Get("字体名称"),
        SkinValueKind.Shadow => "shadow",
        SkinValueKind.String => AppLocalization.Get("字符串"),
        _ => AppLocalization.Get("令牌引用")
    };

    private static InvalidDataException Invalid(string path, string message) =>
        new($"{path}: {AppLocalization.Get(message)}");

    private static InvalidDataException InvalidFormat(string path, string source, params object?[] arguments) =>
        new($"{path}: {AppLocalization.Format(source, arguments)}");
}
