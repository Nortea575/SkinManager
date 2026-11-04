using System.Text.Json;
using SkinManager.Codex;
using SkinManager.Core;

namespace SkinManager.Rendering;

public static class CodexAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string AppFrameSelector => CodexHostContract.AppFrameSelector;
    public static string MainSurfaceSelector => CodexHostContract.MainSurfaceSelector;

    internal const string HomeRootSelector = "[role=\"main\"]";
    internal const string HomeSentinelSelector = "[data-testid=\"home-icon\"]";
    internal const string MessageRoleSelector = "[data-message-author-role]";
    internal const string MessageArticleSelector = "article[data-message-author-role]";
    internal const string CodeBlockSelector = "pre";
    internal const string ComposerHomeVariantSelector = "[data-composer-utility-bar-variant=\"home\"]";
    internal const string ComposerVariantAttribute = "data-composer-utility-bar-variant";
    internal const string ComposerHomeVariant = "home";
    internal const string ComposerBodySelector = "[data-composer-body]";
    internal const string ComposerHomeSurfaceSelector = "[data-composer-utility-bar-variant=\"home\"] [data-composer-body]";
    internal const string ComposerThreadSurfaceSelector = ":not([data-composer-utility-bar-variant=\"home\"])";
    internal const string SkinLinkSelector = "[data-skin-part] a, [data-skin-part] [role=\"link\"]";
    internal const string SelectedElementSelector = "[data-selected], [aria-selected=\"true\"], [aria-current=\"page\"]";

    internal static string SkinPartSelector(string componentId) => $"[data-skin-part=\"{componentId}\"]";

    internal const double MinimumCaptionContrast = 4.5;
    internal const double MaximumCaptionShadeAdjustment = 0.70;
    internal const string NoSkinColorFallbackForLightSymbols = "#1F1F1F";
    internal const string NoSkinColorFallbackForDarkSymbols = "#FFFFFF";
    internal static readonly CssRgba NativeDarkGlyphColor = new(31, 31, 31, byte.MaxValue);
    internal static readonly CssRgba NativeLightGlyphColor = new(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);
    internal static readonly CssRgba DarkCaptionAdjustmentTarget = new(0, 0, 0, byte.MaxValue);
    internal static readonly CssRgba LightCaptionAdjustmentTarget = new(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    internal static readonly string[] CaptionTokenOrder =
    [
        "color.surface.window", "color.accent", "color.surface.sidebar", "color.surface.input",
        "color.surface.elevated", "color.surface.userMessage", "color.surface.assistantMessage",
        "color.surface.code", "color.border.default", "color.text.primary", "color.text.secondary"
    ];

    internal static readonly IReadOnlyDictionary<string, string> PartSelectors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window"] = AppFrameSelector,
            ["content"] = MainSurfaceSelector,
            ["sidebar"] = "aside.app-shell-left-panel",
            ["header"] = "header[data-app-shell-header-edge-scroll]",
            ["home"] = HomeRootSelector,
            ["home.hero"] = "[data-home-ambient-suggestions=\"true\"]",
            ["projectList"] = "[class~=\"group/project-selector\"]",
            ["conversation"] = ".thread-scroll-container",
            ["message.user"] = "article[data-message-author-role=\"user\"]",
            ["message.assistant"] = "article[data-message-author-role=\"assistant\"]",
            ["codeBlock"] = "pre",
            ["composer"] = "[class*=\"_ComposerLayoutRoot_\"]",
            ["composer.editor"] = ".ProseMirror[contenteditable=\"true\"]",
            ["composer.toolbar"] = "[data-composer-footer-responsive]",
            ["button.primary"] = "button[data-variant=\"solid\"]",
            ["button.secondary"] = "button[data-color=\"secondary\"]:not([data-variant=\"solid\"])",
            ["dialog"] = "[role=\"dialog\"]"
        };

    private static readonly IReadOnlyDictionary<string, string> StateSelectorExpressions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selected"] = "[data-selected], [aria-selected=\"true\"], [aria-current=\"page\"]",
            ["hover"] = ":hover",
            ["focused"] = ":focus, :focus-visible, :focus-within",
            ["pressed"] = ":active",
            ["disabled"] = ":disabled, [aria-disabled=\"true\"]"
        };

    internal static readonly IReadOnlyDictionary<string, string> StateSelectors = SkinDefinitionCatalog.States
        .OrderBy(item => item.CssOrder)
        .ToDictionary(item => item.Id, item => StateSelectorExpressions[item.Id], StringComparer.Ordinal);

    internal static readonly IReadOnlyList<string> StatePriority = SkinDefinitionCatalog.States
        .OrderBy(item => item.RuntimePriority)
        .Select(item => item.Id)
        .ToArray();

    internal static readonly string[] PanelComponentIds =
    [
        "window", "content", "sidebar", "header", "message.user", "message.assistant", "codeBlock", "composer", "dialog"
    ];

    internal static readonly HashSet<string> NativeMaterialPanelIds = new(StringComparer.Ordinal)
    {
        "window", "content"
    };

    internal static readonly IReadOnlyDictionary<string, string> SurfaceTargetSuffixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message.user"] = " > div",
            ["message.assistant"] = " > div"
        };

    internal static readonly IReadOnlyDictionary<string, string> PanelBackgroundVariables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window"] = "--app-color-background-shell",
            ["content"] = "--color-background-primary",
            ["sidebar"] = "--app-color-background-elevated-primary",
            ["header"] = "--app-color-background-shell",
            ["home"] = "--color-background-primary",
            ["home.hero"] = "--app-color-background-surface-under",
            ["projectList"] = "--app-color-background-elevated-primary",
            ["conversation"] = "--color-background-primary",
            ["message.user"] = "--user-message-background-color",
            ["message.assistant"] = "--color-background-primary",
            ["codeBlock"] = "--codeblock-background-color",
            ["composer"] = "--color-background-composer-surface",
            ["composer.editor"] = "--color-background-composer-surface",
            ["composer.toolbar"] = "--color-background-composer-surface",
            ["dialog"] = "--app-color-background-elevated-secondary"
        };

    internal static readonly IReadOnlyDictionary<string, string> PanelSurfaceTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window"] = "color.surface.window",
            ["content"] = "color.surface.window",
            ["sidebar"] = "color.surface.sidebar",
            ["header"] = "color.surface.window",
            ["message.user"] = "color.surface.userMessage",
            ["message.assistant"] = "color.surface.assistantMessage",
            ["codeBlock"] = "color.surface.code",
            ["composer"] = "color.surface.input",
            ["dialog"] = "color.surface.elevated"
        };

    internal static readonly IReadOnlyDictionary<string, string[]> NativeColorVariables =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["color.text.primary"] = ["--app-color-text-foreground", "--color-text-primary", "--color-text", "--text-primary"],
            ["color.text.secondary"] = ["--app-color-text-foreground-secondary", "--app-color-text-secondary", "--app-color-text-button-secondary", "--color-text-secondary", "--text-secondary"],
            ["color.text.disabled"] = ["--app-color-text-foreground-tertiary", "--app-color-text-button-tertiary", "--color-text-disabled", "--text-tertiary"],
            ["color.border.default"] = ["--app-color-border", "--color-border", "--border-default"],
            ["color.border.focus"] = ["--app-color-border-focus", "--color-ring", "--color-border-primary-outline", "--color-ring-primary-ghost"],
            ["color.border.error"] = ["--app-color-border-error"],
            ["color.accent"] = ["--app-color-accent-blue", "--app-color-text-accent", "--app-color-icon-accent", "--codex-theme-accent-color", "--codex-theme-accent-text", "--codex-theme-accent-icon"]
        };

    internal static readonly string[] ChatAssistantTextScopeSelectors =
    [
        ":root[data-theme] [data-markdown-text-style=\"assistant-message\"] [class*=\"_TextBase_\"]:not(:where(a, a *, [role=\"link\"], [role=\"link\"] *, [data-d-component=\"link\"], [data-d-component=\"link\"] *, [class*=\"_ExternalMarkdownLink_\"], [class*=\"_ExternalMarkdownLink_\"] *, code, code *, pre, pre *))"
    ];

    internal static readonly string[] ChatAssistantPrimaryTextVariables =
    [
        "--app-color-text-foreground", "--color-text-primary", "--color-text", "--text-primary",
        "--color-text-prose", "--color-text-emphasis"
    ];

    internal static readonly string[] ChatAssistantSecondaryTextVariables =
    [
        "--app-color-text-foreground-secondary", "--app-color-text-secondary", "--app-color-text-button-secondary",
        "--color-text-secondary", "--text-secondary"
    ];

    internal static readonly string[] ChatAssistantDisabledTextVariables =
    [
        "--app-color-text-foreground-tertiary", "--app-color-text-button-tertiary",
        "--color-text-disabled", "--color-text-tertiary", "--text-tertiary"
    ];

    internal static readonly IReadOnlyDictionary<string, string[]> NativeSurfaceVariables =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["color.surface.window"] = ["--app-color-background-shell", "--app-color-background-surface", "--color-background-primary", "--color-surface", "--color-surface-tertiary", "--bg-primary"],
            ["color.surface.sidebar"] = ["--app-color-background-surface-under", "--app-color-background-elevated-primary", "--color-surface-secondary", "--bg-secondary"],
            ["color.surface.elevated"] = ["--app-color-background-elevated-secondary", "--app-color-background-elevated-secondary-opaque", "--app-color-background-elevated-primary-opaque", "--color-surface-elevated", "--color-surface-elevated-secondary", "--bg-elevated-primary", "--bg-elevated-secondary"],
            ["color.surface.input"] = ["--app-color-background-control", "--color-background-activity-input", "--color-background-composer-surface"],
            ["color.surface.userMessage"] = ["--color-background-user-message", "--user-message-background-color"],
            ["color.surface.assistantMessage"] = ["--color-background-secondary"],
            ["color.surface.code"] = ["--codeblock-background-color"]
        };

    internal static readonly IReadOnlyDictionary<string, string[]> NativeScalarVariables =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["font.family"] = ["--font-sans"],
            ["font.size.body"] = ["--text-base"],
            ["font.size.caption"] = ["--text-xs"],
            ["font.weight"] = ["--font-weight-normal"],
            ["font.lineHeight"] = ["--text-base--line-height"],
            ["radius.control"] = ["--button-radius", "--radius-button-action"],
            ["radius.panel"] = ["--app-shell-window-radius"],
            ["border.width"] = ["--border-width-hairline"],
            ["space.control"] = ["--spacing-button-toolbar-inline", "--spacing-token-button-composer"]
        };

    internal static readonly IReadOnlyDictionary<string, string[]> NativePaletteVariables =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["surfaceUnder"] = ["--app-color-background-surface-under", "--app-color-background-elevated-primary", "--app-color-background-elevated-primary-opaque", "--color-surface-secondary", "--bg-secondary"],
            ["surfaceCard"] = ["--app-color-background-card", "--app-color-background-elevated-secondary", "--app-color-background-elevated-secondary-opaque", "--color-surface-elevated", "--color-surface-elevated-secondary", "--color-background-secondary", "--bg-elevated-secondary"],
            ["surfaceElevated"] = ["--app-color-background-elevated-tertiary-opaque", "--color-background-tertiary", "--bg-tertiary", "--codeblock-background-color"],
            ["surfaceControl"] = ["--app-color-background-control", "--color-background-activity-input", "--color-background-composer-surface"],
            ["accent"] = ["--color-background-primary-solid", "--button-background-color", "--app-color-background-button-primary", "--color-ring-primary-solid"],
            ["accentForeground"] = ["--color-text-primary-solid", "--app-color-text-button-primary", "--app-color-text-on-accent"],
            ["accentHover"] = ["--color-background-primary-solid-hover", "--app-color-background-button-primary-hover", "--button-background-color-hover"],
            ["accentPressed"] = ["--color-background-primary-solid-active", "--app-color-background-button-primary-active", "--button-background-color-active"],
            ["accentInactive"] = ["--app-color-background-button-primary-inactive"],
            ["accentSubtle"] = ["--app-color-background-accent", "--codex-theme-accent-background-subtle"],
            ["accentHoverSurface"] = ["--app-color-background-accent-hover", "--codex-theme-accent-background-hover"],
            ["accentPressedSurface"] = ["--app-color-background-accent-active", "--codex-theme-accent-background-pressed"],
            ["userMessage"] = ["--color-background-user-message", "--user-message-background-color"],
            ["ghostHover"] = ["--color-background-primary-ghost-hover"],
            ["ghostPressed"] = ["--color-background-primary-ghost-active"],
            ["ghostText"] = ["--color-text-primary-ghost", "--color-text-primary-ghost-hover"]
        };

    internal static readonly IReadOnlyDictionary<string, int> NativePaletteMixPercentages =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["surfaceUnder"] = 96,
            ["surfaceCard"] = 92,
            ["surfaceElevated"] = 86,
            ["surfaceControl"] = 90,
            ["accentHover"] = 88,
            ["accentPressed"] = 78,
            ["accentSubtle"] = 12,
            ["accentHoverSurface"] = 20,
            ["accentPressedSurface"] = 28,
            ["accentInactiveSurface"] = 10,
            ["textSecondary"] = 72,
            ["textDisabled"] = 48,
            ["defaultBorder"] = 18
        };

    internal static readonly string[] NativeBackgroundFallbackVariables =
    [
        "--app-color-background-shell", "--app-color-background-surface-under"
    ];

    internal const string SurfaceColorFallback = "var(--app-color-background-shell, var(--color-surface, transparent))";
    internal const string SurfaceInkFallback = "var(--app-color-text-foreground, #000000)";
    internal const string AccentInkFallback = "var(--app-color-text-on-accent, currentColor)";

    internal static readonly string[] RuntimeObservedAttributes =
    [
        "data-theme", "class", "data-app-shell-left-panel-appearance", "data-app-shell-sidebar-open",
        "data-app-shell-header-edge-scroll", "data-composer-layout", "data-composer-surface-variant",
        ComposerVariantAttribute, "data-composer-footer-responsive", "data-message-author-role", "data-color",
        "data-composer-submit", "data-testid", "data-selected", "aria-selected", "aria-current",
        "aria-disabled", "aria-label", "disabled", "role", "hidden", "style"
    ];

    internal static readonly IReadOnlyDictionary<string, string> NativeThemeCaptionBackgroundProperty =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dark"] = "forLightSymbols",
            ["light"] = "forDarkSymbols"
        };

    internal static string RuntimeContractJson => JsonSerializer.Serialize(new
    {
        partSelectors = PartSelectors,
        stateSelectors = StateSelectors,
        statePriority = StatePriority,
        homeRootSelector = HomeRootSelector,
        homeSentinelSelector = HomeSentinelSelector,
        messageRoleSelector = MessageRoleSelector,
        messageArticleSelector = MessageArticleSelector,
        codeBlockSelector = CodeBlockSelector,
        composerHomeVariantSelector = ComposerHomeVariantSelector,
        composerVariantAttribute = ComposerVariantAttribute,
        composerHomeVariant = ComposerHomeVariant,
        composerBodySelector = ComposerBodySelector,
        composerHomeSurfaceSelector = ComposerHomeSurfaceSelector,
        composerThreadSurfaceSelector = ComposerThreadSurfaceSelector,
        selectedElementSelector = SelectedElementSelector,
        nativeThemeCaptionBackgroundProperty = NativeThemeCaptionBackgroundProperty,
        nativeBackgroundFallbackVariables = NativeBackgroundFallbackVariables,
        runtimeObservedAttributes = RuntimeObservedAttributes
    }, JsonOptions);

    internal static IReadOnlyList<string> BackgroundSurfaceResetRules =>
    [
        $":where({AppFrameSelector},[data-app-shell-page-surface=\"true\"],[data-app-shell-workspace-row=\"true\"],[data-app-shell-main-surface],aside.app-shell-left-panel,[data-sidebar-navigation-root],.thread-scroll-container,[data-fixture-page-panel]) {{background-color:transparent !important;background-image:none !important;}}",
        ":root[data-theme] [data-thread-scroll-footer=\"true\"] > ._background_18hic_2._surface_18hic_2 {inset-inline:0 !important;translate:calc(0px - var(--thread-content-x, 0px)) 0 !important;}",
        ":root[data-theme] .thread-scroll-container ._background_18hic_2._fade_18hic_2 {inset-inline:0 !important;translate:calc(0px - var(--thread-content-x, 0px)) 0 !important;}"
    ];
}
