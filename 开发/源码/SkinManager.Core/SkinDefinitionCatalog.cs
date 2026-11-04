using System.Collections.ObjectModel;

namespace SkinManager.Core;

public enum SkinEditorFieldKind
{
    Unsupported,
    Color,
    Number,
    Text
}

public enum SkinFieldDisplayTransform
{
    None,
    PositionPercent,
    TransparencyPercent
}

public enum SkinCssValueFormat
{
    Default,
    BlurFunction,
    NumberOrLength
}

public sealed record SkinFieldDefinition(
    string Key,
    SkinValueKind Kind,
    string Label,
    string? Unit = null,
    string? DefaultValue = null,
    SkinEditorFieldKind EditorKind = SkinEditorFieldKind.Unsupported,
    SkinFieldDisplayTransform DisplayTransform = SkinFieldDisplayTransform.None,
    bool EditorDefaultVisible = false,
    bool AllowNoneText = false,
    string? CssProperty = null,
    SkinCssValueFormat CssValueFormat = SkinCssValueFormat.Default,
    bool UsesSurfaceTarget = false,
    string? PaletteLabel = null,
    int PaletteOrder = int.MaxValue);

public sealed record SkinComponentDefinition(string Id, string Label, bool EditorVisible = true);

public sealed record SkinStateDefinition(string Id, string Label, int EditorOrder, int RuntimePriority, int CssOrder);

public static class SkinDefinitionCatalog
{
    private static readonly SkinFieldDefinition[] TokenDefinitionArray =
    [
        new("color.accent", SkinValueKind.Color, "强调色", DefaultValue: "#3B82F6", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 10),
        new("color.surface.window", SkinValueKind.Color, "窗口背景", DefaultValue: "#F7F9FC", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteLabel: "窗口表面", PaletteOrder: 0),
        new("color.text.primary", SkinValueKind.Color, "主要文字", DefaultValue: "#17202B", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 7),
        new("color.surface.sidebar", SkinValueKind.Color, "侧栏表面", DefaultValue: "#F0F3F7", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 1),
        new("color.surface.elevated", SkinValueKind.Color, "浮层表面", DefaultValue: "#FFFFFF", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 2),
        new("color.surface.input", SkinValueKind.Color, "输入区表面", DefaultValue: "#EEF2F6", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 3),
        new("color.surface.userMessage", SkinValueKind.Color, "用户消息表面", DefaultValue: "#E9EFF5", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 4),
        new("color.surface.assistantMessage", SkinValueKind.Color, "助手消息表面", DefaultValue: "#FFFFFF", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 5),
        new("color.surface.code", SkinValueKind.Color, "代码表面", DefaultValue: "#F1F3F5", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 6),
        new("color.text.secondary", SkinValueKind.Color, "次级文字", DefaultValue: "#586372", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 8),
        new("color.text.disabled", SkinValueKind.Color, "禁用文字", DefaultValue: "#9AA4AE", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 9),
        new("color.text.link", SkinValueKind.Color, "链接文字", DefaultValue: "#2563EB", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 10),
        new("color.border.default", SkinValueKind.Color, "边框", DefaultValue: "#D4DBE2", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 10),
        new("color.border.focus", SkinValueKind.Color, "焦点边框", DefaultValue: "#3B82F6", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 10),
        new("color.border.error", SkinValueKind.Color, "错误边框", DefaultValue: "#DC2626", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, PaletteOrder: 10),
        new("font.family", SkinValueKind.FontFamily, "系统字体", DefaultValue: "Segoe UI", EditorKind: SkinEditorFieldKind.Text, EditorDefaultVisible: true),
        new("font.size.body", SkinValueKind.Length, "正文字号（px）", "px", "14", SkinEditorFieldKind.Number, EditorDefaultVisible: true),
        new("font.size.caption", SkinValueKind.Length, "说明字号（px）", "px", "12", SkinEditorFieldKind.Number, EditorDefaultVisible: true),
        new("radius.control", SkinValueKind.Length, "控件圆角（px）", "px", "10", SkinEditorFieldKind.Number, EditorDefaultVisible: true),
        new("radius.panel", SkinValueKind.Length, "面板圆角（px）", "px", "14", SkinEditorFieldKind.Number, EditorDefaultVisible: true),
        new("material.fillOpacity", SkinValueKind.Ratio, "面板透明度（%）", "%", "75", SkinEditorFieldKind.Number, SkinFieldDisplayTransform.TransparencyPercent, EditorDefaultVisible: true),
        new("font.weight", SkinValueKind.Number, "字体字重", "", null, SkinEditorFieldKind.Number),
        new("font.lineHeight", SkinValueKind.Number, "字体行高", "", null, SkinEditorFieldKind.Number),
        new("border.width", SkinValueKind.Length, "边框宽度（px）", "px", null, SkinEditorFieldKind.Number),
        new("space.control", SkinValueKind.Length, "控件间距（px）", "px", null, SkinEditorFieldKind.Number),
        new("material.blur", SkinValueKind.Length, "材质模糊（px）", "px", null, SkinEditorFieldKind.Number),
        new("shadow.panel", SkinValueKind.Shadow, "面板阴影", DefaultValue: null, EditorKind: SkinEditorFieldKind.Text, AllowNoneText: true)
    ];

    private static readonly SkinFieldDefinition[] ComponentPropertyDefinitionArray =
    [
        new("background", SkinValueKind.Color, "表面颜色", DefaultValue: "#EEF2F6", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, AllowNoneText: true, CssProperty: "background-color", UsesSurfaceTarget: true),
        new("color", SkinValueKind.Color, "文字颜色", DefaultValue: "#17202B", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, CssProperty: "color"),
        new("fillOpacity", SkinValueKind.Ratio, "填充透明度（%）", "%", "0", SkinEditorFieldKind.Number, SkinFieldDisplayTransform.TransparencyPercent, EditorDefaultVisible: true),
        new("borderColor", SkinValueKind.Color, "边框颜色", DefaultValue: "#D4DBE2", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, CssProperty: "border-color", UsesSurfaceTarget: true),
        new("borderWidth", SkinValueKind.Length, "边框宽度（px）", "px", "1", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "border-width", UsesSurfaceTarget: true),
        new("borderTopColor", SkinValueKind.Color, "上边框颜色", EditorKind: SkinEditorFieldKind.Color, CssProperty: "border-top-color", UsesSurfaceTarget: true),
        new("borderRightColor", SkinValueKind.Color, "右边框颜色", EditorKind: SkinEditorFieldKind.Color, CssProperty: "border-right-color", UsesSurfaceTarget: true),
        new("borderBottomColor", SkinValueKind.Color, "下边框颜色", EditorKind: SkinEditorFieldKind.Color, CssProperty: "border-bottom-color", UsesSurfaceTarget: true),
        new("borderLeftColor", SkinValueKind.Color, "左边框颜色", EditorKind: SkinEditorFieldKind.Color, CssProperty: "border-left-color", UsesSurfaceTarget: true),
        new("borderTopWidth", SkinValueKind.Length, "上边框宽度（px）", "px", null, SkinEditorFieldKind.Number, CssProperty: "border-top-width", UsesSurfaceTarget: true),
        new("borderRightWidth", SkinValueKind.Length, "右边框宽度（px）", "px", null, SkinEditorFieldKind.Number, CssProperty: "border-right-width", UsesSurfaceTarget: true),
        new("borderBottomWidth", SkinValueKind.Length, "下边框宽度（px）", "px", null, SkinEditorFieldKind.Number, CssProperty: "border-bottom-width", UsesSurfaceTarget: true),
        new("borderLeftWidth", SkinValueKind.Length, "左边框宽度（px）", "px", null, SkinEditorFieldKind.Number, CssProperty: "border-left-width", UsesSurfaceTarget: true),
        new("radius", SkinValueKind.Length, "圆角（px）", "px", "8", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "border-radius", UsesSurfaceTarget: true),
        new("fontFamily", SkinValueKind.FontFamily, "字体", DefaultValue: "Segoe UI", EditorKind: SkinEditorFieldKind.Text, EditorDefaultVisible: true, CssProperty: "font-family"),
        new("fontSize", SkinValueKind.Length, "字号（px）", "px", "14", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "font-size"),
        new("fontWeight", SkinValueKind.Number, "字重（100–900）", "", "400", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "font-weight"),
        new("lineHeight", SkinValueKind.Number, "行高", "", "1.4", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "line-height", CssValueFormat: SkinCssValueFormat.NumberOrLength),
        new("padding", SkinValueKind.Length, "内边距（px）", "px", "8", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "padding", UsesSurfaceTarget: true),
        new("gap", SkinValueKind.Length, "间距（px）", "px", "8", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "gap"),
        new("blur", SkinValueKind.Length, "模糊（px）", "px", "0", SkinEditorFieldKind.Number, EditorDefaultVisible: true, CssProperty: "backdrop-filter", CssValueFormat: SkinCssValueFormat.BlurFunction, UsesSurfaceTarget: true),
        new("shadow", SkinValueKind.Shadow, "阴影", AllowNoneText: true, CssProperty: "box-shadow", UsesSurfaceTarget: true)
    ];

    private static readonly SkinFieldDefinition[] BackgroundPropertyDefinitionArray =
    [
        new("color", SkinValueKind.Color, "背景底色", DefaultValue: "#F7F9FC", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, AllowNoneText: true),
        new("image", SkinValueKind.String, "背景图片", EditorKind: SkinEditorFieldKind.Unsupported),
        new("fit", SkinValueKind.String, "背景适配", EditorKind: SkinEditorFieldKind.Unsupported),
        new("focusX", SkinValueKind.Ratio, "水平焦点位置（%）", "%", "50", SkinEditorFieldKind.Number, SkinFieldDisplayTransform.PositionPercent, EditorDefaultVisible: true),
        new("focusY", SkinValueKind.Ratio, "垂直焦点位置（%）", "%", "50", SkinEditorFieldKind.Number, SkinFieldDisplayTransform.PositionPercent, EditorDefaultVisible: true),
        new("opacity", SkinValueKind.Ratio, "图片透明度（%）", "%", "0", SkinEditorFieldKind.Number, SkinFieldDisplayTransform.TransparencyPercent, EditorDefaultVisible: true),
        new("overlayColor", SkinValueKind.Color, "覆盖层颜色", DefaultValue: "#000000", EditorKind: SkinEditorFieldKind.Color, EditorDefaultVisible: true, AllowNoneText: true),
        new("overlayOpacity", SkinValueKind.Ratio, "覆盖层透明度（%）", "%", "100", SkinEditorFieldKind.Number, SkinFieldDisplayTransform.TransparencyPercent, EditorDefaultVisible: true)
    ];

    private static readonly SkinComponentDefinition[] ComponentDefinitionArray =
    [
        new("window", "窗口"),
        new("content", "内容区"),
        new("sidebar", "侧栏"),
        new("header", "顶部栏"),
        new("home", "首页"),
        new("home.hero", "首页欢迎区"),
        new("projectList", "项目列表"),
        new("conversation", "对话区"),
        new("message.user", "用户消息"),
        new("message.assistant", "助手消息"),
        new("codeBlock", "代码块"),
        new("composer", "输入面板"),
        new("composer.editor", "输入文字区"),
        new("composer.toolbar", "输入工具栏"),
        new("button.primary", "主按钮"),
        new("button.secondary", "次按钮"),
        new("dialog", "对话框")
    ];

    private static readonly SkinStateDefinition[] StateDefinitionArray =
    [
        new("hover", "悬停", 0, 3, 1),
        new("focused", "焦点", 1, 2, 2),
        new("pressed", "按下", 2, 1, 3),
        new("disabled", "禁用", 3, 0, 4),
        new("selected", "选中", 4, 4, 0)
    ];

    private static readonly IReadOnlyDictionary<string, SkinFieldDefinition> TokenIndex = Index(TokenDefinitionArray);
    private static readonly IReadOnlyDictionary<string, SkinFieldDefinition> ComponentPropertyIndex = Index(ComponentPropertyDefinitionArray);
    private static readonly IReadOnlyDictionary<string, SkinFieldDefinition> BackgroundPropertyIndex = Index(BackgroundPropertyDefinitionArray);
    private static readonly IReadOnlyDictionary<string, SkinComponentDefinition> ComponentIndex =
        new ReadOnlyDictionary<string, SkinComponentDefinition>(ComponentDefinitionArray.ToDictionary(item => item.Id, StringComparer.Ordinal));
    private static readonly IReadOnlyDictionary<string, SkinStateDefinition> StateIndex =
        new ReadOnlyDictionary<string, SkinStateDefinition>(StateDefinitionArray.ToDictionary(item => item.Id, StringComparer.Ordinal));

    public static IReadOnlyList<SkinFieldDefinition> Tokens { get; } = Array.AsReadOnly(TokenDefinitionArray);
    public static IReadOnlyList<SkinFieldDefinition> ComponentProperties { get; } = Array.AsReadOnly(ComponentPropertyDefinitionArray);
    public static IReadOnlyList<SkinFieldDefinition> BackgroundProperties { get; } = Array.AsReadOnly(BackgroundPropertyDefinitionArray);
    public static IReadOnlyList<SkinComponentDefinition> Components { get; } = Array.AsReadOnly(ComponentDefinitionArray);
    public static IReadOnlyList<SkinStateDefinition> States { get; } = Array.AsReadOnly(StateDefinitionArray);

    public static IEnumerable<SkinFieldDefinition> DefaultTokenEditorFields => Tokens.Where(item => item.EditorDefaultVisible);
    public static IEnumerable<SkinFieldDefinition> DefaultComponentEditorFields => ComponentProperties.Where(item => item.EditorDefaultVisible);
    public static IEnumerable<SkinFieldDefinition> ExistingComponentEditorFields => ComponentProperties.Where(item => item.EditorKind != SkinEditorFieldKind.Unsupported);
    public static IEnumerable<SkinFieldDefinition> DefaultBackgroundEditorFields => BackgroundProperties.Where(item => item.EditorDefaultVisible);

    public static bool TryGetToken(string key, out SkinFieldDefinition definition) => TokenIndex.TryGetValue(key, out definition!);
    public static bool TryGetComponentProperty(string key, out SkinFieldDefinition definition) => ComponentPropertyIndex.TryGetValue(key, out definition!);
    public static bool TryGetBackgroundProperty(string key, out SkinFieldDefinition definition) => BackgroundPropertyIndex.TryGetValue(key, out definition!);
    public static bool TryGetComponent(string id, out SkinComponentDefinition definition) => ComponentIndex.TryGetValue(id, out definition!);
    public static bool TryGetState(string id, out SkinStateDefinition definition) => StateIndex.TryGetValue(id, out definition!);

    public static SkinFieldDefinition? GetEditorField(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            return null;
        }

        if (path.Contains("tokens", StringComparer.Ordinal) && TryGetToken(path[^1], out var token))
        {
            return token;
        }

        if (path.Contains("components", StringComparer.Ordinal) && TryGetComponentProperty(path[^1], out var componentProperty))
        {
            return componentProperty;
        }

        if (path.Contains("background", StringComparer.Ordinal) && TryGetBackgroundProperty(path[^1], out var backgroundProperty))
        {
            return backgroundProperty;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, SkinFieldDefinition> Index(IEnumerable<SkinFieldDefinition> definitions) =>
        new ReadOnlyDictionary<string, SkinFieldDefinition>(definitions.ToDictionary(item => item.Key, StringComparer.Ordinal));
}
