using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkinManager.Core;

namespace SkinManager.Rendering;

public sealed record NativeFillOpacity(
    double? Default,
    IReadOnlyDictionary<string, double> States,
    bool NoFill,
    IReadOnlyDictionary<string, bool> NoFillStates,
    IReadOnlyDictionary<string, bool> BackgroundStates,
    string? NativeBackgroundVariable,
    bool HasNativeMaterialBackground,
    bool HasExplicitBackground,
    bool HasExplicitColorToken);

public sealed record NativeBackgroundStyle(
    string? Color,
    string? ImageUrl,
    string? Fit,
    double? FocusX,
    double? FocusY,
    double? Opacity,
    string? OverlayColor,
    double? OverlayOpacity);

public sealed record NativeCaptionBackgroundPlan(
    string? ForLightSymbols,
    string? ForDarkSymbols);

public sealed record NativeSkinStylePayload(
    string Css,
    IReadOnlyDictionary<string, NativeFillOpacity> FillOpacity,
    NativeBackgroundStyle? Background,
    NativeCaptionBackgroundPlan? NativeCaption);

public static class NativeSkinStyleBuilder
{
    private static readonly HashSet<string> SurfaceProperties = SkinDefinitionCatalog.ComponentProperties
        .Where(item => item.UsesSurfaceTarget)
        .Select(item => item.Key)
        .ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> CssProperties = SkinDefinitionCatalog.ComponentProperties
        .Where(item => item.CssProperty is not null)
        .ToDictionary(item => item.Key, item => item.CssProperty!, StringComparer.Ordinal);

    public static NativeSkinStylePayload Build(ResolvedSkin? skin, string? imageUrl)
    {
        var declarations = new List<string>();
        var fillOpacity = BuildFillOpacity(skin);
        var background = BuildBackgroundStyle(skin?.Background, imageUrl);
        var nativeCaption = BuildNativeCaptionBackgroundPlan(skin);

        if (skin is not null)
        {
            if (background is not null)
            {
                AppendBackgroundSurfaceRules(declarations);
            }

            AppendNativePalette(skin, declarations);
            AppendTokenVariables(skin, declarations);
            AppendChatAssistantTextAliases(skin, declarations);
            foreach (var componentId in SkinDefinitionCatalog.Components.Select(item => item.Id))
            {
                if (!skin.Components.TryGetValue(componentId, out var component))
                {
                    continue;
                }

                AppendComponentRules(componentId, component, declarations);
            }
        }

        var stylesheet = declarations.Count == 0
            ? string.Empty
            : $"@layer skin-manager-native {{{Environment.NewLine}{string.Join(Environment.NewLine, declarations)}{Environment.NewLine}}}";
        return new NativeSkinStylePayload(stylesheet, fillOpacity, background, nativeCaption);
    }

    private static NativeCaptionBackgroundPlan? BuildNativeCaptionBackgroundPlan(ResolvedSkin? skin)
    {
        if (skin is null)
        {
            return null;
        }

        var rawColors = new List<string>();
        if (skin.Components.TryGetValue("window", out var window) &&
            window.Properties.TryGetValue("background", out var windowBackground) &&
            windowBackground.Kind == SkinValueKind.Color &&
            TryGetText(windowBackground, out var windowBackgroundText) &&
            !IsNoneText(windowBackgroundText))
        {
            rawColors.Add(windowBackgroundText);
        }

        AddTokenColor(skin, rawColors, CodexAdapter.CaptionTokenOrder[0]);
        AddBackgroundColor(skin.Background?.Color, rawColors);
        foreach (var token in CodexAdapter.CaptionTokenOrder.Skip(1))
        {
            AddTokenColor(skin, rawColors, token);
        }

        var parsed = rawColors
            .Select(value => CssColorParser.TryParse(value, out var color) && color.Alpha > 0
                ? (CssRgba?)(color with { Alpha = byte.MaxValue })
                : null)
            .Where(color => color.HasValue)
            .Select(color => color!.Value)
            .ToArray();

        var candidates = parsed.ToList();

        var forDarkSymbols = SelectCaptionBackground(candidates, CodexAdapter.NativeDarkGlyphColor, CodexAdapter.LightCaptionAdjustmentTarget);
        var forLightSymbols = SelectCaptionBackground(candidates, CodexAdapter.NativeLightGlyphColor, CodexAdapter.DarkCaptionAdjustmentTarget);
        return new NativeCaptionBackgroundPlan(
            forLightSymbols ?? CodexAdapter.NoSkinColorFallbackForLightSymbols,
            forDarkSymbols ?? CodexAdapter.NoSkinColorFallbackForDarkSymbols);
    }

    private static string? SelectCaptionBackground(IReadOnlyList<CssRgba> candidates, CssRgba glyphColor, CssRgba adjustmentTarget)
    {
        foreach (var candidate in candidates)
        {
            if (CaptionContrast(candidate, glyphColor) >= CodexAdapter.MinimumCaptionContrast)
            {
                return FormatCssColor(candidate);
            }
        }

        foreach (var candidate in candidates)
        {
            var maximum = Blend(candidate, adjustmentTarget, CodexAdapter.MaximumCaptionShadeAdjustment);
            if (CaptionContrast(maximum, glyphColor) < CodexAdapter.MinimumCaptionContrast)
            {
                continue;
            }

            var low = 0d;
            var high = CodexAdapter.MaximumCaptionShadeAdjustment;
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var middle = (low + high) / 2;
                if (CaptionContrast(Blend(candidate, adjustmentTarget, middle), glyphColor) >= CodexAdapter.MinimumCaptionContrast)
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }
            }

            var adjusted = Blend(candidate, adjustmentTarget, Math.Min(CodexAdapter.MaximumCaptionShadeAdjustment, high + 0.005));
            return CaptionContrast(adjusted, glyphColor) >= CodexAdapter.MinimumCaptionContrast ? FormatCssColor(adjusted) : FormatCssColor(maximum);
        }

        return null;
    }

    private static void AddTokenColor(ResolvedSkin skin, ICollection<string> colors, string token)
    {
        if (TryGetColor(skin, token, out var color))
        {
            colors.Add(color);
        }
    }

    private static void AddBackgroundColor(string? color, ICollection<string> colors)
    {
        if (!string.IsNullOrWhiteSpace(color) && !IsNoneText(color))
        {
            colors.Add(color);
        }
    }

    private static double CaptionContrast(CssRgba surface, CssRgba glyph)
    {
        var surfaceLuminance = RelativeLuminance(surface);
        var glyphLuminance = RelativeLuminance(glyph);
        return (Math.Max(surfaceLuminance, glyphLuminance) + 0.05) / (Math.Min(surfaceLuminance, glyphLuminance) + 0.05);
    }

    private static CssRgba Blend(CssRgba color, CssRgba target, double amount)
    {
        static byte BlendChannel(byte source, byte destination, double ratio) => (byte)Math.Round(
            source + ((destination - source) * ratio),
            MidpointRounding.AwayFromZero);

        return new CssRgba(
            BlendChannel(color.Red, target.Red, amount),
            BlendChannel(color.Green, target.Green, amount),
            BlendChannel(color.Blue, target.Blue, amount),
            byte.MaxValue);
    }

    private static string FormatCssColor(CssRgba color) =>
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static NativeBackgroundStyle? BuildBackgroundStyle(ResolvedBackground? background, string? imageUrl)
    {
        if (background is null)
        {
            return null;
        }

        var color = IsNoneText(background.Color) ? "transparent" : background.Color;
        var overlayColor = IsNoneText(background.OverlayColor) ? null : background.OverlayColor;
        if (color is null && imageUrl is null && overlayColor is null)
        {
            return null;
        }

        return new NativeBackgroundStyle(
            color,
            imageUrl,
            background.Fit,
            background.FocusX,
            background.FocusY,
            background.Opacity,
            overlayColor,
            overlayColor is null ? null : background.OverlayOpacity);
    }

    public static string BuildApplyScript(NativeSkinStylePayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $$"""
            (() => {
              const payload = {{payloadJson}};
              const codex = {{CodexAdapter.RuntimeContractJson}};
              {{SkinFillEngine.Source}}
              const root = document.documentElement;
              if (payload.background) root.setAttribute("data-skin-background-active", "true");
              else root.removeAttribute("data-skin-background-active");
              const styleId = "skin-manager-native-style";
              let style = document.getElementById(styleId);
              if (!style) {
                style = document.createElement("style");
                style.id = styleId;
                document.head.appendChild(style);
              }
              style.textContent = payload.css || "";

              const managerKey = "__SKIN_MANAGER_NATIVE_FILL__";
              const manager = window[managerKey] || (window[managerKey] = {
                originalBackground: new WeakMap(),
                fillApplied: new WeakMap(),
                elements: new Set(),
                bindings: new WeakMap(),
                backgroundCache: new Map(),
                ownerHandlers: new Map(),
                ownerSurfaces: new Map(),
                ownerFrames: new Map(),
                payload: null,
                update: null,
              });
              manager.ownerHandlers ||= new Map();
              manager.ownerSurfaces ||= new Map();
              manager.ownerFrames ||= new Map();
              manager.fillOriginals ||= manager.originalBackground || new WeakMap();
              manager.originalBackground = manager.fillOriginals;
              manager.fillApplied ||= new WeakMap();
              manager.backgroundCache ||= new Map();
              manager.payload = payload;
              const fillEngine = createSkinFillEngine({
                document,
                getComputedStyle: (element) => getComputedStyle(element),
                root,
                statePriority: codex.statePriority,
                stateSelectors: codex.stateSelectors,
                fillOriginals: manager.fillOriginals,
                fillApplied: manager.fillApplied,
                nativeBackgroundCache: manager.backgroundCache,
                isBackgroundActive: () => root.getAttribute("data-skin-background-active") === "true",
              });
              const attachOwner = (owner) => {
                if (manager.ownerHandlers.has(owner)) return;
                const updateOwnerFill = () => {
                  if (manager.ownerFrames.has(owner)) return;
                  const frame = requestAnimationFrame(() => {
                    manager.ownerFrames.delete(owner);
                    for (const surface of manager.ownerSurfaces.get(owner) || []) {
                      const binding = manager.bindings.get(surface);
                      if (!binding) continue;
                      const config = manager.payload?.fillOpacity?.[binding.part];
                      if (config) fillEngine.applyFill(surface, owner, config);
                    }
                  });
                  manager.ownerFrames.set(owner, frame);
                };
                for (const eventName of ["pointerover", "pointerout", "pointerdown", "pointerup", "focus", "blur", "focusin", "focusout"]) {
                  owner.addEventListener(eventName, updateOwnerFill, true);
                }
                manager.ownerHandlers.set(owner, updateOwnerFill);
              };
              manager.update = () => {
                for (const surface of manager.elements) fillEngine.restoreFill(surface);
                for (const [owner, handler] of manager.ownerHandlers) {
                  for (const eventName of ["pointerover", "pointerout", "pointerdown", "pointerup", "focus", "blur", "focusin", "focusout"]) {
                    owner.removeEventListener(eventName, handler, true);
                  }
                  const frame = manager.ownerFrames.get(owner);
                  if (frame !== undefined) cancelAnimationFrame(frame);
                }
                manager.ownerHandlers.clear();
                manager.ownerFrames.clear();
                manager.ownerSurfaces.clear();
                manager.elements.clear();
                manager.bindings = new WeakMap();
                manager.backgroundCache.clear();
                for (const [part, config] of Object.entries(payload.fillOpacity || {})) {
                  const ownerSelector = "[data-skin-part=\"" + CSS.escape(part) + "\"]";
                  for (const owner of document.querySelectorAll(ownerSelector)) {
                    const surface = part === "composer"
                      ? owner.matches(codex.composerHomeVariantSelector)
                        ? owner.querySelector(":scope " + codex.composerBodySelector)
                        : owner
                      : ["message.user", "message.assistant"].includes(part)
                        ? owner.querySelector(":scope > div")
                        : owner;
                    if (!surface) continue;
                    manager.elements.add(surface);
                    manager.bindings.set(surface, { part, owner });
                    if (!manager.ownerSurfaces.has(owner)) manager.ownerSurfaces.set(owner, new Set());
                    manager.ownerSurfaces.get(owner).add(surface);
                    attachOwner(owner);
                    fillEngine.applyFill(surface, owner, config);
                  }
                }
              };
              manager.update();

              const backgroundId = "skin-manager-background";
              let background = document.getElementById(backgroundId);
              if (!payload.background) {
                background?.remove();
                root.removeAttribute("data-skin-background-active");
                return;
              }
              if (!background) {
                background = document.createElement("div");
                background.id = backgroundId;
                background.setAttribute("aria-hidden", "true");
                Object.assign(background.style, {
                  position: "fixed",
                  inset: "0",
                  zIndex: "0",
                  pointerEvents: "none",
                  overflow: "hidden",
                });
                const shell = document.querySelector(codex.partSelectors.window);
                if (shell?.parentElement === document.body) document.body.insertBefore(background, shell);
                else document.body.prepend(background);
              }
              root.setAttribute("data-skin-background-active", "true");
              const computedRoot = getComputedStyle(root);
              const fallback = codex.nativeBackgroundFallbackVariables.map((variable) => computedRoot.getPropertyValue(variable).trim()).find(Boolean)
                || getComputedStyle(document.body).backgroundColor;
              background.style.backgroundColor = payload.background.color || fallback;

              let image = background.querySelector(".skin-manager-image");
              if (payload.background.imageUrl) {
                if (!image) {
                  image = document.createElement("div");
                  image.className = "skin-manager-image";
                  Object.assign(image.style, { position: "absolute", inset: "0", backgroundRepeat: "no-repeat" });
                  background.appendChild(image);
                }
                image.style.backgroundImage = `url(${JSON.stringify(payload.background.imageUrl)})`;
                image.style.backgroundSize = payload.background.fit || "";
                image.style.backgroundPosition = payload.background.focusX !== null && payload.background.focusY !== null
                  ? `${payload.background.focusX * 100}% ${payload.background.focusY * 100}%`
                  : payload.background.focusX !== null
                    ? `${payload.background.focusX * 100}%`
                    : payload.background.focusY !== null
                      ? `center ${payload.background.focusY * 100}%`
                      : "";
                image.style.opacity = payload.background.opacity ?? "";
              } else {
                image?.remove();
              }

              let overlay = background.querySelector(".skin-manager-overlay");
              if (payload.background.overlayColor) {
                if (!overlay) {
                  overlay = document.createElement("div");
                  overlay.className = "skin-manager-overlay";
                  Object.assign(overlay.style, { position: "absolute", inset: "0" });
                  background.appendChild(overlay);
                }
                overlay.style.backgroundColor = payload.background.overlayColor;
                overlay.style.opacity = payload.background.overlayOpacity ?? "";
              } else {
                overlay?.remove();
              }
            })()
            """;
    }

    private static Dictionary<string, NativeFillOpacity> BuildFillOpacity(ResolvedSkin? skin)
    {
        var result = new Dictionary<string, NativeFillOpacity>(StringComparer.Ordinal);
        if (skin is null)
        {
            return result;
        }

        var externalOverride = skin.PanelOpacityOverride;
        var tokenOpacity = skin.Tokens.TryGetValue("material.fillOpacity", out var token)
            ? ReadRatio(token)
            : null;
        foreach (var componentId in SkinDefinitionCatalog.Components.Select(item => item.Id))
        {
            skin.Components.TryGetValue(componentId, out var component);
            var isPanel = CodexAdapter.PanelComponentIds.Contains(componentId, StringComparer.Ordinal);
            var noFill = component is not null &&
                component.Properties.TryGetValue("background", out var backgroundValue) && IsNone(backgroundValue);
            var noFillStates = component?.States
                .Where(state => state.Value.TryGetValue("background", out var stateBackground) && IsNone(stateBackground))
                .ToDictionary(state => state.Key, _ => true, StringComparer.Ordinal)
                ?? new Dictionary<string, bool>(StringComparer.Ordinal);
            var backgroundStates = component?.States
                .Where(state => state.Value.ContainsKey("background"))
                .ToDictionary(state => state.Key, _ => true, StringComparer.Ordinal)
                ?? new Dictionary<string, bool>(StringComparer.Ordinal);
            var hasExplicitBackground = component is not null &&
                (component.Properties.TryGetValue("background", out var explicitBackground) && !IsNone(explicitBackground)
                 || component.States.Values.Any(state => state.TryGetValue("background", out var stateBackground) && !IsNone(stateBackground)));
            var hasExplicitColorToken = CodexAdapter.PanelSurfaceTokens.TryGetValue(componentId, out var surfaceToken) && skin.Tokens.ContainsKey(surfaceToken);
            var hasNativeMaterialBackground = CodexAdapter.NativeMaterialPanelIds.Contains(componentId);
            var hasPanelFill = hasNativeMaterialBackground || hasExplicitBackground || hasExplicitColorToken;
            var panelOverride = isPanel && hasPanelFill ? externalOverride : null;
            var componentOpacity = component is not null && component.Properties.TryGetValue("fillOpacity", out var value)
                ? ReadRatio(value)
                : null;
            var defaultOpacity = panelOverride ?? componentOpacity ?? (isPanel && hasPanelFill ? tokenOpacity : null);
            var states = new Dictionary<string, double>(StringComparer.Ordinal);
            if (component is not null)
            {
                foreach (var stateName in CodexAdapter.StateSelectors.Keys)
                {
                    if (component.States.TryGetValue(stateName, out var state) && state.TryGetValue("fillOpacity", out var stateValue))
                    {
                        var stateOpacity = panelOverride ?? ReadRatio(stateValue);
                        if (stateOpacity.HasValue)
                        {
                            states[stateName] = stateOpacity.Value;
                        }
                    }
                    else if (panelOverride.HasValue)
                    {
                        states[stateName] = panelOverride.Value;
                    }
                }
            }
            else if (panelOverride.HasValue)
            {
                foreach (var stateName in CodexAdapter.StateSelectors.Keys)
                {
                    states[stateName] = panelOverride.Value;
                }
            }

            if (defaultOpacity.HasValue || states.Count > 0)
            {
                result[componentId] = new NativeFillOpacity(
                    defaultOpacity,
                    states,
                    noFill,
                    noFillStates,
                    backgroundStates,
                    CodexAdapter.PanelBackgroundVariables.GetValueOrDefault(componentId),
                    hasNativeMaterialBackground,
                    hasExplicitBackground,
                    hasExplicitColorToken);
            }
        }

        return result;
    }

    private static void AppendTokenVariables(ResolvedSkin skin, ICollection<string> declarations)
    {
        foreach (var pair in skin.Tokens.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!TryBuildValue(pair.Value, pair.Key == "shadow.panel", out var value))
            {
                continue;
            }

            if (pair.Key == "material.blur" && pair.Value.Kind == SkinValueKind.Length && pair.Value.Number is { } blurTokenLength)
            {
                value = $"blur({FormatLength(blurTokenLength)})";
            }

            var tokenName = "--skin-token-" + Regex.Replace(pair.Key, "[^a-zA-Z0-9_-]", "-");
            declarations.Add($":root[data-theme] {{{tokenName}:{value} !important;}}");
            if (CodexAdapter.NativeSurfaceVariables.TryGetValue(pair.Key, out var surfaceVariables) && pair.Value.Kind == SkinValueKind.Color)
            {
                AppendVariableAliases(surfaceVariables, value, declarations);
            }
            else if (CodexAdapter.NativeColorVariables.TryGetValue(pair.Key, out var colorVariables) && pair.Value.Kind == SkinValueKind.Color)
            {
                AppendVariableAliases(colorVariables, value, declarations);
            }
            else if (CodexAdapter.NativeScalarVariables.TryGetValue(pair.Key, out var scalarVariables))
            {
                AppendVariableAliases(scalarVariables, value, declarations);
            }

            if (pair.Value.Kind == SkinValueKind.Color && pair.Key.StartsWith("color.surface.", StringComparison.Ordinal))
            {
                foreach (var componentId in CodexAdapter.PanelSurfaceTokens
                    .Where(entry => string.Equals(entry.Value, pair.Key, StringComparison.Ordinal))
                    .Select(entry => entry.Key))
                {
                    declarations.Add($"{SurfaceSelector(componentId)} {{background-color:{value} !important;}}");
                }
            }

            if (pair.Key == "color.text.link" && pair.Value.Kind == SkinValueKind.Color)
            {
            declarations.Add($"{CodexAdapter.SkinLinkSelector} {{color:{value} !important;}}");
            }

            if (pair.Key == "color.accent" && pair.Value.Kind == SkinValueKind.Color)
            {
            declarations.Add($"{CodexAdapter.SelectedElementSelector} {{color:{value} !important;}}");
            }

            if (pair.Key == "radius.control")
            {
                foreach (var selector in new[]
                {
                    CodexAdapter.SkinPartSelector("button.primary"),
                    CodexAdapter.SkinPartSelector("button.secondary"),
                    SurfaceSelector("composer")
                })
                {
                    declarations.Add($"{selector} {{border-radius:{value} !important;}}");
                }
            }

            if (pair.Key == "radius.panel")
            {
                foreach (var panel in CodexAdapter.PanelSurfaceTokens.Keys)
                {
                    declarations.Add($"{SurfaceSelector(panel)} {{border-radius:{value} !important;}}");
                }
            }

            if (pair.Key == "material.blur" && pair.Value.Kind == SkinValueKind.Length && pair.Value.Number is { } blurLength)
            {
                var blur = $"blur({FormatLength(blurLength)})";
                foreach (var componentId in CodexAdapter.PanelSurfaceTokens.Keys)
                {
                    var selector = SurfaceSelector(componentId);
                    declarations.Add($"{selector} {{backdrop-filter:{blur} !important;-webkit-backdrop-filter:{blur} !important;}}");
                }
            }

            if (pair.Key == "shadow.panel")
            {
                var shadow = TryBuildValue(pair.Value, true, out var shadowValue) ? shadowValue : value;
                foreach (var panel in CodexAdapter.PanelSurfaceTokens.Keys)
                {
                    declarations.Add($"{SurfaceSelector(panel)} {{box-shadow:{shadow} !important;}}");
                }
            }
        }
    }

    private static void AppendNativePalette(ResolvedSkin skin, ICollection<string> declarations)
    {
        var hasSurface = TryGetColor(skin, "color.surface.window", out var surface);
        var hasInk = TryGetColor(skin, "color.text.primary", out var ink);
        var hasAccent = TryGetColor(skin, "color.accent", out var accent);
        var surfaceValue = hasSurface ? surface : CodexAdapter.SurfaceColorFallback;

        if (hasSurface)
        {
            var surfaceInk = hasInk ? ink : CodexAdapter.SurfaceInkFallback;
            AppendPaletteMix("surfaceUnder", surface, surfaceInk, declarations);
            AppendPaletteMix("surfaceCard", surface, surfaceInk, declarations);
            AppendPaletteMix("surfaceElevated", surface, surfaceInk, declarations);
            AppendPaletteMix("surfaceControl", surface, surfaceInk, declarations);
        }

        if (hasInk)
        {
            if (!skin.Tokens.ContainsKey("color.text.secondary"))
            {
                AppendVariableAliases(
                    CodexAdapter.NativeColorVariables["color.text.secondary"],
                    BuildDerivedTextColor(ink, surfaceValue, CodexAdapter.NativePaletteMixPercentages["textSecondary"]),
                    declarations);
            }

            if (!skin.Tokens.ContainsKey("color.text.disabled"))
            {
                AppendVariableAliases(
                    CodexAdapter.NativeColorVariables["color.text.disabled"],
                    BuildDerivedTextColor(ink, surfaceValue, CodexAdapter.NativePaletteMixPercentages["textDisabled"]),
                    declarations);
            }

            if (!skin.Tokens.ContainsKey("color.border.default"))
            {
                AppendVariableAliases(
                    CodexAdapter.NativeColorVariables["color.border.default"],
                    BuildPaletteMix("defaultBorder", ink, surfaceValue),
                    declarations);
            }
        }

        if (hasAccent)
        {
            var accentForeground = SelectAccentForeground(accent, hasSurface ? surface : null);
            AppendPaletteAliases("accent", accent, declarations);
            AppendPaletteAliases("accentForeground", accentForeground, declarations);
            AppendPaletteMix("accentHover", accent, surfaceValue, declarations);
            AppendPaletteMix("accentPressed", accent, surfaceValue, declarations);
            AppendPaletteMix("accentInactive", accent, surfaceValue, declarations, "accentInactiveSurface");
            AppendPaletteMix("accentSubtle", accent, surfaceValue, declarations);
            AppendPaletteMix("accentHoverSurface", accent, surfaceValue, declarations);
            AppendPaletteMix("accentPressedSurface", accent, surfaceValue, declarations);
            AppendPaletteMix("userMessage", accent, surfaceValue, declarations, "accentSubtle");
            AppendPaletteMix("ghostHover", accent, surfaceValue, declarations, "accentHoverSurface");
            AppendPaletteMix("ghostPressed", accent, surfaceValue, declarations, "accentPressedSurface");
            AppendPaletteAliases("ghostText", accent, declarations);
        }
    }

    private static void AppendChatAssistantTextAliases(ResolvedSkin skin, ICollection<string> declarations)
    {
        var hasPrimaryToken = TryGetColor(skin, "color.text.primary", out var primary);
        if (skin.Components.TryGetValue("message.assistant", out var assistantComponent) &&
            assistantComponent.Properties.TryGetValue("color", out var assistantColor) &&
            TryBuildComponentValue("color", assistantColor, out var componentColor) &&
            !IsNoneText(componentColor))
        {
            primary = componentColor;
            hasPrimaryToken = true;
        }

        var hasSurface = TryGetColor(skin, "color.surface.window", out var surface);
        var surfaceValue = hasSurface ? surface : CodexAdapter.SurfaceColorFallback;

        var hasSecondary = TryGetColor(skin, "color.text.secondary", out var secondary);
        var hasDisabled = TryGetColor(skin, "color.text.disabled", out var disabled);
        if (hasPrimaryToken)
        {
            if (!hasSecondary)
            {
                secondary = BuildDerivedTextColor(primary, surfaceValue, CodexAdapter.NativePaletteMixPercentages["textSecondary"]);
                hasSecondary = true;
            }

            if (!hasDisabled)
            {
                disabled = BuildDerivedTextColor(primary, surfaceValue, CodexAdapter.NativePaletteMixPercentages["textDisabled"]);
                hasDisabled = true;
            }
        }

        if (!hasPrimaryToken && !hasSecondary && !hasDisabled)
        {
            return;
        }

        if (hasPrimaryToken)
        {
            AppendChatAssistantTextVariableAliases(CodexAdapter.ChatAssistantPrimaryTextVariables, primary, declarations);
        }

        if (hasSecondary)
        {
            AppendChatAssistantTextVariableAliases(CodexAdapter.ChatAssistantSecondaryTextVariables, secondary, declarations);
        }

        if (hasDisabled)
        {
            AppendChatAssistantTextVariableAliases(CodexAdapter.ChatAssistantDisabledTextVariables, disabled, declarations);
        }
    }

    private static void AppendChatAssistantTextVariableAliases(
        IEnumerable<string> variables,
        string value,
        ICollection<string> declarations)
    {
        declarations.Add(
            $"{string.Join(", ", CodexAdapter.ChatAssistantTextScopeSelectors)} {{{string.Join(';', variables.Select(variable => $"{variable}:{value} !important"))};}}");
    }

    private static string BuildDerivedTextColor(string foreground, string surface, int foregroundPercent) =>
        $"color-mix(in srgb, {foreground} {foregroundPercent}%, {surface})";

    private static string SelectAccentForeground(string accent, string? surface)
    {
        if (!CssColorParser.TryParse(accent, out var accentColor))
        {
            return CodexAdapter.AccentInkFallback;
        }

        if (surface is not null && CssColorParser.TryParse(surface, out var surfaceColor))
        {
            accentColor = Composite(accentColor, surfaceColor);
        }

        if (accentColor.Alpha < byte.MaxValue)
        {
            accentColor = Composite(accentColor, new CssRgba(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue));
        }

        var luminance = RelativeLuminance(accentColor);
        var blackContrast = (luminance + 0.05) / 0.05;
        var whiteContrast = 1.05 / (luminance + 0.05);
        return whiteContrast > blackContrast ? "#ffffff" : "#000000";
    }

    private static CssRgba Composite(CssRgba foreground, CssRgba background)
    {
        var foregroundAlpha = foreground.Alpha / 255d;
        var backgroundAlpha = background.Alpha / 255d;
        var outputAlpha = foregroundAlpha + (backgroundAlpha * (1 - foregroundAlpha));
        if (outputAlpha <= 0)
        {
            return new CssRgba(0, 0, 0, 0);
        }

        byte Blend(byte foregroundChannel, byte backgroundChannel) => (byte)Math.Round(
            ((foregroundChannel * foregroundAlpha) + (backgroundChannel * backgroundAlpha * (1 - foregroundAlpha))) / outputAlpha,
            MidpointRounding.AwayFromZero);

        return new CssRgba(
            Blend(foreground.Red, background.Red),
            Blend(foreground.Green, background.Green),
            Blend(foreground.Blue, background.Blue),
            (byte)Math.Round(outputAlpha * byte.MaxValue, MidpointRounding.AwayFromZero));
    }

    private static double RelativeLuminance(CssRgba color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.Red)) + (0.7152 * Linearize(color.Green)) + (0.0722 * Linearize(color.Blue));
    }

    private static bool TryGetColor(ResolvedSkin skin, string token, out string value)
    {
        if (skin.Tokens.TryGetValue(token, out var skinValue) &&
            skinValue.Kind == SkinValueKind.Color &&
            TryGetText(skinValue, out value) &&
            !IsNoneText(value))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void AppendComponentRules(string componentId, ResolvedComponentStyle component, ICollection<string> declarations)
    {
        var selector = CodexAdapter.SkinPartSelector(componentId);
        AppendProperties(componentId, selector, [""], component.Properties, declarations);
        foreach (var stateName in CodexAdapter.StateSelectors.Keys)
        {
            if (!component.States.TryGetValue(stateName, out var state) || state.Count == 0)
            {
                continue;
            }

            var stateSuffixes = CodexAdapter.StateSelectors[stateName].Split(", ", StringSplitOptions.RemoveEmptyEntries);
            AppendProperties(componentId, selector, stateSuffixes, state, declarations);
        }
    }

    private static void AppendProperties(
        string componentId,
        string selector,
        IReadOnlyList<string> stateSuffixes,
        IReadOnlyDictionary<string, SkinValue> properties,
        ICollection<string> declarations)
    {
        var groups = properties
            .Where(pair => CssProperties.ContainsKey(pair.Key) && TryBuildComponentValue(pair.Key, pair.Value, out _))
            .GroupBy(pair => (componentId == "composer" || CodexAdapter.SurfaceTargetSuffixes.ContainsKey(componentId)) && SurfaceProperties.Contains(pair.Key));
        foreach (var group in groups)
        {
            var targetSelectors = stateSuffixes.SelectMany(suffix => group.Key
                ? SurfaceSelectors(componentId, selector + suffix).ToArray()
                : new[] { selector + suffix });
            var rules = new List<string>();
            foreach (var pair in group.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!CssProperties.TryGetValue(pair.Key, out var cssProperty) || !TryBuildComponentValue(pair.Key, pair.Value, out var cssValue))
                {
                    continue;
                }

                rules.Add($"{cssProperty}:{cssValue} !important");
                if (pair.Key == "background" && IsNone(pair.Value))
                {
                    rules.Add("background-image:none !important");
                }
                if (pair.Key == "blur")
                {
                    rules.Add($"-webkit-backdrop-filter:{cssValue} !important");
                }
            }

            if (rules.Count > 0)
            {
                declarations.Add($"{string.Join(", ", targetSelectors)} {{{string.Join(';', rules)};}}");
            }
        }
    }

    private static string SurfaceSelector(string componentId) =>
        string.Join(", ", SurfaceSelectors(componentId, CodexAdapter.SkinPartSelector(componentId)));

    private static IReadOnlyList<string> SurfaceSelectors(string componentId, string ownerSelector)
    {
        if (componentId == "composer")
        {
            return
            [
                ownerSelector + CodexAdapter.ComposerHomeSurfaceSelector,
                ownerSelector + CodexAdapter.ComposerThreadSurfaceSelector
            ];
        }

        return
        [
            ownerSelector + (CodexAdapter.SurfaceTargetSuffixes.TryGetValue(componentId, out var suffix) ? suffix : string.Empty)
        ];
    }

    private static bool TryBuildComponentValue(string property, SkinValue value, out string result)
    {
        result = string.Empty;
        if (!SkinDefinitionCatalog.TryGetComponentProperty(property, out var definition) || definition.CssProperty is null)
        {
            return false;
        }

        switch (definition.Kind)
        {
            case SkinValueKind.Color:
                if (value.Kind != SkinValueKind.Color || !TryGetText(value, out result))
                {
                    return false;
                }

                if (definition.CssProperty == "background-color" && IsNone(value))
                {
                    result = "transparent";
                }

                return true;
            case SkinValueKind.FontFamily:
                if (value.Kind != SkinValueKind.FontFamily || !TryGetText(value, out var family))
                {
                    return false;
                }

                result = FormatFontFamily(family);
                return true;
            case SkinValueKind.Shadow:
                return TryBuildValue(value, definition.AllowNoneText, out result);
            case SkinValueKind.Length:
                if (!TryBuildLength(value, out var length))
                {
                    return false;
                }

                result = definition.CssValueFormat == SkinCssValueFormat.BlurFunction ? $"blur({length})" : length;
                return true;
            case SkinValueKind.Number:
                if (definition.CssValueFormat == SkinCssValueFormat.NumberOrLength)
                {
                    return (value.Kind is SkinValueKind.Number or SkinValueKind.Ratio or SkinValueKind.Length) && TryBuildNumberOrLength(value, out result);
                }

                return value.Kind == SkinValueKind.Number && TryBuildNumber(value, out result);
            default:
                return false;
        }
    }

    private static bool TryBuildValue(SkinValue value, bool allowNone, out string result)
    {
        result = string.Empty;
        if (allowNone && IsNoneText(value.Text))
        {
            result = "none";
            return true;
        }

        if (value.Kind == SkinValueKind.FontFamily && TryGetText(value, out var family))
        {
            result = FormatFontFamily(family);
            return true;
        }

        if (value.Kind == SkinValueKind.Color && TryGetText(value, out result))
        {
            return true;
        }

        if (value.Kind == SkinValueKind.Shadow && value.Shadow is { } shadow)
        {
            var opacity = FormatNumber(shadow.Opacity);
            var color = $"color-mix(in srgb, {shadow.Color} {opacity}%, transparent)";
            result = $"{FormatLength(shadow.X)} {FormatLength(shadow.Y)} {FormatLength(shadow.Blur)} {FormatLength(shadow.Spread)} {color}";
            return true;
        }

        if (value.Number is { } number)
        {
            result = value.Kind == SkinValueKind.Length ? FormatLength(number) : FormatNumber(number);
            return true;
        }

        return false;
    }

    private static bool TryBuildLength(SkinValue value, out string result)
    {
        result = string.Empty;
        if (value.Kind == SkinValueKind.Length && value.Number is { } number)
        {
            result = FormatLength(number);
            return true;
        }

        return false;
    }

    private static bool TryBuildNumber(SkinValue value, out string result)
    {
        result = string.Empty;
        if (value.Kind == SkinValueKind.Number && value.Number is { } number)
        {
            result = FormatNumber(number);
            return true;
        }

        if (value.Kind == SkinValueKind.Length && value.Number is { } length)
        {
            result = FormatNumber(length);
            return true;
        }

        return false;
    }

    private static bool TryBuildNumberOrLength(SkinValue value, out string result)
    {
        result = string.Empty;
        if (value.Number is not { } number)
        {
            return false;
        }

        result = value.Kind == SkinValueKind.Length ? FormatLength(number) : FormatNumber(number);
        return true;
    }

    private static bool TryGetText(SkinValue value, out string result)
    {
        result = value.Text ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool IsNone(SkinValue value) =>
        value.Kind == SkinValueKind.Color && IsNoneText(value.Text);

    private static bool IsNoneText(string? value) =>
        string.Equals(value?.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    private static double? ReadRatio(SkinValue value) => value.Number is { } number && double.IsFinite(number)
        ? Math.Clamp(number, 0, 1)
        : null;

    private static string BuildPaletteMix(string name, string foreground, string background) =>
        $"color-mix(in srgb, {foreground} {CodexAdapter.NativePaletteMixPercentages[name]}%, {background})";

    private static void AppendPaletteMix(
        string variableGroup,
        string foreground,
        string background,
        ICollection<string> declarations,
        string? mixName = null) =>
        AppendPaletteAliases(variableGroup, BuildPaletteMix(mixName ?? variableGroup, foreground, background), declarations);

    private static void AppendPaletteAliases(string variableGroup, string value, ICollection<string> declarations) =>
        AppendVariableAliases(CodexAdapter.NativePaletteVariables[variableGroup], value, declarations);

    private static void AppendVariableAliases(IEnumerable<string> variables, string value, ICollection<string> declarations)
    {
        declarations.Add($":root[data-theme] {{{string.Join(';', variables.Select(variable => $"{variable}:{value} !important"))};}}");
    }

    private static void AppendBackgroundSurfaceRules(ICollection<string> declarations)
    {
        foreach (var rule in CodexAdapter.BackgroundSurfaceResetRules)
        {
            declarations.Add(rule);
        }
    }

    private static string FormatLength(double value) => FormatNumber(value) + "px";

    private static string FormatNumber(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string QuoteCssString(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '\\' or '"')
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.Append('"').ToString();
    }

    private static string FormatFontFamily(string value)
    {
        var genericFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui",
            "ui-serif", "ui-sans-serif", "ui-monospace", "ui-rounded", "math", "emoji", "fangsong"
        };
        return string.Join(", ", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(family =>
            {
                var isQuoted = family.Length >= 2 && (family[0] == '\'' || family[0] == '"') && family[^1] == family[0];
                var unquoted = isQuoted ? family[1..^1] : family;
                return genericFamilies.Contains(unquoted) ? unquoted : QuoteCssString(unquoted);
            }));
    }
}
