using System.Security.Cryptography;
using System.Text.Json;
using SkinManager.Core;

namespace SkinManager.Rendering;

public sealed record PreviewRuntimePackage(
    bool Enabled,
    string Appearance,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, NativeSkinStylePayload>> Styles,
    IReadOnlyDictionary<string, string> Images,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, NativeFillOpacity>>> PanelFillTargets);

public static class SkinRuntimeCompiler
{
    private static readonly JsonSerializerOptions RuntimeJsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildRuntimeScript(SkinPackage package, double? userTransparency = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (userTransparency.HasValue && (!double.IsFinite(userTransparency.Value) || userTransparency.Value is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(userTransparency), AppLocalization.Get("透明度必须介于 0 到 100 之间。"));
        }

        var panelOpacity = userTransparency.HasValue ? (100 - userTransparency.Value) / 100 : (double?)null;
        var runtime = CompilePackage(package, panelOpacity,
            (_, imagePath, bytes) => $"data:{SkinPackageReader.GetImageMediaType(imagePath)};base64,{Convert.ToBase64String(bytes)}");
        return BuildRuntimeSource(JsonSerializer.Serialize(runtime, RuntimeJsonOptions), keepEmptyPackage: false);
    }

    public static PreviewRuntimePackage BuildPreviewRuntimePackage(SkinPackage package, Func<string, string, byte[], string> imageReference)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(imageReference);

        var panelFillTargets = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, NativeFillOpacity>>>(StringComparer.Ordinal);
        var runtime = CompilePackage(package, panelOpacity: null, imageReference, panelFillTargets);
        return new PreviewRuntimePackage(runtime.Enabled, runtime.Appearance, runtime.Styles, runtime.Images, panelFillTargets);
    }

    public static string BuildPreviewRuntimeTemplate() =>
        BuildRuntimeSource("window.__SKIN_PREVIEW_RUNTIME_PACKAGE__", keepEmptyPackage: true);

    public static string BuildRemoveScript() => "(() => { window.__SKIN_MANAGER_RUNTIME__?.remove?.(); })();";

    private static RuntimePackage CompilePackage(
        SkinPackage package,
        double? panelOpacity,
        Func<string, string, byte[], string> imageReference,
        Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, NativeFillOpacity>>>? panelFillTargets = null)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var styles = new Dictionary<string, IReadOnlyDictionary<string, NativeSkinStylePayload>>(StringComparer.Ordinal);

        foreach (var appearance in new[] { SkinAppearance.Light, SkinAppearance.Dark })
        {
            var appearanceKey = appearance == SkinAppearance.Light ? "light" : "dark";
            var scenes = new Dictionary<string, NativeSkinStylePayload>(StringComparer.Ordinal);
            var sceneTargets = panelFillTargets is not null ? new Dictionary<string, IReadOnlyDictionary<string, NativeFillOpacity>>(StringComparer.Ordinal) : null;
            foreach (var scene in new[] { SkinScene.Home, SkinScene.Conversation })
            {
                var sceneKey = scene == SkinScene.Home ? "home" : "conversation";
                var resolved = SkinResolver.Resolve(package, appearance, scene, panelOpacity);
                string? imageKey = null;
                if (resolved.Background is { ImagePath: { } imagePath } background)
                {
                    var bytes = background.ImageBytes ?? (package.Files.TryGetValue(imagePath, out var packageBytes) ? packageBytes : null);
                    if (bytes is null || bytes.Length == 0)
                    {
                        throw new InvalidDataException(AppLocalization.Format("皮肤背景资源“{0}”没有可用的图片数据。", imagePath));
                    }

                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    imageKey = "sha256-" + hash;
                    if (!images.ContainsKey(imageKey)) images.Add(imageKey, imageReference(imageKey, imagePath, bytes));
                }

                scenes[sceneKey] = NativeSkinStyleBuilder.Build(resolved, imageKey);
                if (sceneTargets is not null)
                {
                    var opacityTemplate = SkinResolver.Resolve(package, appearance, scene, panelOpacityOverride: 1);
                    sceneTargets[sceneKey] = NativeSkinStyleBuilder.BuildFillOpacity(opacityTemplate);
                }
            }

            styles[appearanceKey] = scenes;
            if (sceneTargets is not null) panelFillTargets!.Add(appearanceKey, sceneTargets);
        }

        var hasOverrides = package.Appearance != SkinAppearance.System || styles.Values
            .SelectMany(sceneStyles => sceneStyles.Values)
            .Any(payload => payload.Css.Length > 0 || payload.FillOpacity.Count > 0 || payload.Background is not null);
        return new RuntimePackage(hasOverrides, package.Appearance.ToString().ToLowerInvariant(), styles, images);
    }

    private static string BuildRuntimeSource(string packageExpression, bool keepEmptyPackage) => RuntimeScriptTemplate
        .Replace("__KEEP_EMPTY_PACKAGE__", keepEmptyPackage ? "true" : "false", StringComparison.Ordinal)
        .Replace("__CODEX_ADAPTER_JSON__", CodexAdapter.RuntimeContractJson, StringComparison.Ordinal)
        .Replace("__SKIN_FILL_ENGINE_SOURCE__", SkinFillEngine.Source, StringComparison.Ordinal)
        .Replace("__SKIN_RUNTIME_PACKAGE_JSON__", packageExpression, StringComparison.Ordinal);

    private sealed record RuntimePackage(
        bool Enabled,
        string Appearance,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, NativeSkinStylePayload>> Styles,
        IReadOnlyDictionary<string, string> Images);

    private static readonly string RuntimeScriptTemplate = """
        (() => {
          const runtimePackage = __SKIN_RUNTIME_PACKAGE_JSON__;
          const codex = __CODEX_ADAPTER_JSON__;
          __SKIN_FILL_ENGINE_SOURCE__
          const managerKey = "__SKIN_MANAGER_RUNTIME__";
          const existing = window[managerKey];
          if (!runtimePackage.enabled && !__KEEP_EMPTY_PACKAGE__) {
            existing?.remove?.();
            return;
          }
          if (existing) {
            if (existing.appearanceVersion === 4 && existing.injectionVersion === 1 && typeof existing.apply === "function") {
              existing.apply(runtimePackage);
              return;
            }
            existing.remove?.();
          }

          const observedAttributes = codex.runtimeObservedAttributes;
          const manager = {
            package: runtimePackage,
            started: false,
            removed: false,
            observer: null,
            readyObserver: null,
            appearanceObserver: null,
            appearanceExpectedWrites: [],
            rootThemeOriginal: null,
            appearanceMedia: null,
            appearanceHandler: null,
            readyHandler: null,
            reconcileFrame: null,
            pendingDiscoverRoots: new Set(),
            pendingReleaseRoots: new Set(),
            pendingReleaseOwners: new Set(),
            pendingRebindOwners: new Set(),
            pendingNeedsRefresh: false,
            activeKey: null,
            active: null,
            styleElement: null,
            captionBackgroundElement: null,
            nativeThemeVariant: null,
            nativeThemeUnsubscribe: null,
            windowControlsOverlay: null,
            windowControlsGeometryHandler: null,
            windowResizeHandler: null,
            parts: new Map(),
            partOriginals: new Map(),
            blobUrls: new Map(),
            appearanceVersion: 4,
            injectionVersion: 1,
          };

          const getStyle = (element) => getComputedStyle(element);
          const background = createSkinBackground({
            document, codex, getComputedStyle: getStyle,
            className: "skin-manager-runtime-background",
            resolveImageUrl: (key) => manager.imageUrl(key),
          });
          const fills = createSkinFillBindings({
            document, codex, getComputedStyle: getStyle,
            surfaceFor: (owner, part) => manager.surfaceFor(owner, part),
            isBackgroundActive: background.isActive,
          });

          const partSelectors = codex.partSelectors;

          const isElement = (value) => value && value.nodeType === 1;
          const includesScope = (scope, element) => scope === element || (scope?.contains && scope.contains(element));
          const collect = (scope, selector) => {
            const found = [];
            if (isElement(scope) && scope.matches(selector)) found.push(scope);
            if (scope?.querySelectorAll) found.push(...scope.querySelectorAll(selector));
            return found;
          };
          const isManagerOwned = (element) => isElement(element) && element.closest("[data-skin-manager-owned=\"true\"]") !== null;
          const isVisible = (element) => {
            if (!element || !element.isConnected || element.closest("[hidden], [aria-hidden=\"true\"]")) return false;
            const style = getComputedStyle(element);
            if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse") return false;
            const rects = typeof element.getClientRects === "function" ? element.getClientRects() : null;
            return !rects || rects.length > 0;
          };

          manager.hideNativeCaptionBackground = () => {
            manager.captionBackgroundElement?.remove();
            manager.captionBackgroundElement = null;
          };

          manager.updateNativeCaptionBackground = () => {
            if (manager.removed) return;
            const overlay = manager.windowControlsOverlay;
            const shell = document.querySelector(partSelectors.window);
            const captionColors = manager.active?.nativeCaption;
            const captionProperty = codex.nativeThemeCaptionBackgroundProperty[manager.nativeThemeVariant];
            const captionColor = captionProperty ? captionColors?.[captionProperty] : null;
            if (!manager.active || !shell || !overlay?.visible || !captionColor
              || (manager.nativeThemeVariant !== "light" && manager.nativeThemeVariant !== "dark")) {
              manager.hideNativeCaptionBackground();
              return;
            }

            let rect;
            try { rect = overlay.getTitlebarAreaRect(); }
            catch (_) { manager.hideNativeCaptionBackground(); return; }
            const left = Number(rect?.x) + Number(rect?.width);
            const top = Number(rect?.y);
            const width = Number(window.innerWidth) - left;
            const height = Number(rect?.height);
            if (![left, top, width, height].every(Number.isFinite) || width <= 0 || height <= 0) {
              manager.hideNativeCaptionBackground();
              return;
            }

            let background = manager.captionBackgroundElement;
            if (!background) {
              background = document.createElement("div");
              background.className = "skin-manager-native-caption-background";
              background.setAttribute("data-skin-manager-owned", "true");
              background.setAttribute("data-skin-manager-caption-background", "true");
              background.setAttribute("aria-hidden", "true");
              Object.assign(background.style, {
                position: "fixed", zIndex: "2147483647", pointerEvents: "none", overflow: "hidden",
              });
              document.body.appendChild(background);
              manager.captionBackgroundElement = background;
            }
            background.style.left = `${left}px`;
            background.style.top = `${top}px`;
            background.style.width = `${width}px`;
            background.style.height = `${height}px`;
            background.style.right = "auto";
            background.style.bottom = "auto";
            background.style.backgroundColor = captionColor;
          };

          manager.applyNativeThemeVariant = (variant) => {
            manager.nativeThemeVariant = variant === "light" || variant === "dark" ? variant : null;
            manager.updateNativeCaptionBackground();
          };

          manager.installNativeCaptionBackground = () => {
            if (manager.nativeThemeUnsubscribe || manager.windowControlsOverlay) return;
            if (typeof navigator === "undefined") return;
            if (!manager.active || !document.querySelector(partSelectors.window)) return;
            const overlay = navigator.windowControlsOverlay;
            const bridge = window.electronBridge;
            if (!overlay || typeof overlay.getTitlebarAreaRect !== "function"
              || typeof overlay.addEventListener !== "function" || typeof overlay.removeEventListener !== "function"
              || !bridge || typeof bridge.getSystemThemeVariant !== "function"
              || typeof bridge.subscribeToSystemThemeVariant !== "function") return;

            let unsubscribe = null;
            try {
              unsubscribe = bridge.subscribeToSystemThemeVariant((variant) => manager.applyNativeThemeVariant(variant));
              if (typeof unsubscribe !== "function") return;
              manager.nativeThemeUnsubscribe = unsubscribe;
              manager.windowControlsOverlay = overlay;
              manager.windowControlsGeometryHandler = () => manager.updateNativeCaptionBackground();
              manager.windowResizeHandler = manager.windowControlsGeometryHandler;
              overlay.addEventListener("geometrychange", manager.windowControlsGeometryHandler);
              window.addEventListener("resize", manager.windowResizeHandler);
              manager.applyNativeThemeVariant(bridge.getSystemThemeVariant());
              manager.updateNativeCaptionBackground();
            } catch (_) {
              if (manager.nativeThemeUnsubscribe) manager.disposeNativeCaptionBackground();
              else {
                try { unsubscribe?.(); } catch (_) { }
                manager.hideNativeCaptionBackground();
              }
            }
          };

          manager.disposeNativeCaptionBackground = () => {
            try { manager.nativeThemeUnsubscribe?.(); } catch (_) { }
            manager.nativeThemeUnsubscribe = null;
            if (manager.windowControlsOverlay && manager.windowControlsGeometryHandler) {
              try { manager.windowControlsOverlay.removeEventListener("geometrychange", manager.windowControlsGeometryHandler); } catch (_) { }
            }
            if (manager.windowResizeHandler) {
              try { window.removeEventListener("resize", manager.windowResizeHandler); } catch (_) { }
            }
            manager.windowControlsOverlay = null;
            manager.windowControlsGeometryHandler = null;
            manager.windowResizeHandler = null;
            manager.nativeThemeVariant = null;
            manager.hideNativeCaptionBackground();
          };

          manager.start = () => {
            if (manager.started || manager.removed) return;
            if (!document.documentElement || !document.head || !document.body) {
              if (!manager.readyObserver) {
                manager.readyObserver = new MutationObserver(() => manager.start());
                manager.readyObserver.observe(document, { childList: true, subtree: true });
                manager.readyHandler = () => manager.start();
                document.addEventListener("DOMContentLoaded", manager.readyHandler, { once: true });
              }
              return;
            }
            manager.started = true;
            manager.readyObserver?.disconnect();
            manager.readyObserver = null;
            if (manager.readyHandler) document.removeEventListener("DOMContentLoaded", manager.readyHandler);
            manager.readyHandler = null;
            manager.discover(document.documentElement);
            manager.installAppearanceObserver();
            manager.installObserver();
            manager.installAppearanceListener();
            manager.refreshActive(true);
          };

          manager.apply = (nextPackage) => {
            if (manager.removed) return;
            manager.drainAppearanceMutations(false);
            manager.package = nextPackage;
            manager.syncImageUrls();
            if (manager.started) manager.refreshActive(true);
            else manager.start();
          };

          manager.installObserver = () => {
            if (manager.observer || !document.documentElement) return;
            manager.observer = new MutationObserver((mutations) => {
              for (const mutation of mutations) {
                if (mutation.type === "childList") {
                  for (const removed of mutation.removedNodes) {
                    if (isElement(removed) && !isManagerOwned(removed)) manager.pendingReleaseRoots.add(removed);
                  }
                  for (const added of mutation.addedNodes) {
                    if (isElement(added) && !isManagerOwned(added)) {
                      manager.pendingDiscoverRoots.add(added);
                    }
                  }
                  const targetPart = isElement(mutation.target) ? manager.parts.get(mutation.target) : null;
                  if (targetPart === "composer" || targetPart === "message.user" || targetPart === "message.assistant") {
                    manager.pendingRebindOwners.add(mutation.target);
                  }
                  manager.pendingNeedsRefresh = true;
                  continue;
                }

                const target = mutation.target;
                if (!isElement(target) || isManagerOwned(target)) continue;
                if (mutation.attributeName === "style") {
                  const binding = fills.bindings.get(target);
                  if (!fills.isAppliedFill(target)) {
                    if (binding) fills.scheduleOwnerFill(binding.owner);
                    manager.pendingNeedsRefresh = true;
                  }
                  continue;
                }

                if ((target === document.documentElement || target === document.body)
                  && ["class", "data-theme"].includes(mutation.attributeName)) {
                  manager.pendingNeedsRefresh = true;
                  continue;
                }

                const currentPart = manager.parts.get(target);
                if (currentPart && !manager.matchesPart(target, currentPart)) {
                  manager.pendingReleaseOwners.add(target);
                  manager.pendingDiscoverRoots.add(target);
                } else if (!currentPart) {
                  manager.pendingDiscoverRoots.add(target);
                }
                if (mutation.attributeName === codex.composerVariantAttribute
                  && currentPart === "composer") manager.pendingRebindOwners.add(target);
                const binding = fills.bindings.get(target);
                if (binding) fills.scheduleOwnerFill(binding.owner);
                manager.pendingNeedsRefresh = true;
              }
              manager.scheduleReconcile();
            });
            manager.observer.observe(document.documentElement, {
              childList: true,
              subtree: true,
              attributes: true,
              attributeFilter: observedAttributes,
            });
          };

          manager.scheduleReconcile = () => {
            if (manager.reconcileFrame !== null || manager.removed) return;
            manager.reconcileFrame = requestAnimationFrame(() => {
              manager.reconcileFrame = null;
              for (const root of manager.pendingReleaseRoots) {
                if (!root.isConnected && !isManagerOwned(root)) manager.releaseSubtree(root);
              }
              manager.pendingReleaseRoots.clear();
              for (const owner of manager.pendingReleaseOwners) manager.releaseOwner(owner);
              manager.pendingReleaseOwners.clear();

              const roots = Array.from(manager.pendingDiscoverRoots);
              manager.pendingDiscoverRoots.clear();
              for (const root of roots) {
                if (!isManagerOwned(root) && root.isConnected && !roots.some((other) => other !== root && other.contains(root))) {
                  manager.discover(root);
                }
              }

              for (const owner of manager.pendingRebindOwners) manager.rebindOwner(owner);
              manager.pendingRebindOwners.clear();
              if (manager.pendingNeedsRefresh) manager.refreshActive(false);
              manager.pendingNeedsRefresh = false;
              if (manager.active?.background) background.sync(manager.active.background);
              if (manager.active && !manager.windowControlsOverlay) manager.installNativeCaptionBackground();
              manager.updateNativeCaptionBackground();
            });
          };

          manager.installAppearanceListener = () => {
            if (manager.appearanceMedia || typeof window.matchMedia !== "function") return;
            manager.appearanceMedia = window.matchMedia("(prefers-color-scheme: dark)");
            manager.appearanceHandler = () => manager.refreshActive(false);
            manager.appearanceMedia.addEventListener("change", manager.appearanceHandler);
          };

          manager.readRootTheme = () => {
            const root = document.documentElement;
            return root?.hasAttribute("data-theme")
              ? { present: true, value: root.getAttribute("data-theme") }
              : { present: false, value: null };
          };

          manager.writeRootTheme = (snapshot) => {
            const root = document.documentElement;
            if (!root) return;
            const oldValue = root.hasAttribute("data-theme") ? root.getAttribute("data-theme") : null;
            const newValue = snapshot.present ? (snapshot.value ?? "") : null;
            if (oldValue === newValue) return;
            if (manager.appearanceObserver) manager.appearanceExpectedWrites.push({ oldValue, newValue });
            if (snapshot.present) root.setAttribute("data-theme", newValue);
            else root.removeAttribute("data-theme");
          };

          manager.processAppearanceMutations = (mutations, reapply) => {
            const root = document.documentElement;
            if (!root || mutations.length === 0) return;
            const finalValue = root.hasAttribute("data-theme") ? root.getAttribute("data-theme") : null;
            let expectedIndex = 0;
            let hostChanged = false;
            for (let index = 0; index < mutations.length; index++) {
              const mutation = mutations[index];
              const oldValue = mutation.oldValue;
              const newValue = index + 1 < mutations.length ? mutations[index + 1].oldValue : finalValue;
              const expected = manager.appearanceExpectedWrites[expectedIndex];
              if (expected && expected.oldValue === oldValue && expected.newValue === newValue) {
                expectedIndex++;
                continue;
              }
              if (manager.rootThemeOriginal) {
                manager.rootThemeOriginal = { present: newValue !== null, value: newValue };
                hostChanged = true;
              }
            }
            if (expectedIndex > 0) manager.appearanceExpectedWrites.splice(0, expectedIndex);
            const appearance = manager.package.appearance;
            if (hostChanged && reapply && manager.rootThemeOriginal && (appearance === "light" || appearance === "dark")) {
              manager.writeRootTheme({ present: true, value: appearance });
            }
          };

          manager.drainAppearanceMutations = (reapply) => {
            const mutations = manager.appearanceObserver?.takeRecords() || [];
            manager.processAppearanceMutations(mutations, reapply);
          };

          manager.installAppearanceObserver = () => {
            if (manager.appearanceObserver || !document.documentElement) return;
            manager.appearanceObserver = new MutationObserver((mutations) => {
              manager.processAppearanceMutations(mutations, true);
            });
            manager.appearanceObserver.observe(document.documentElement, {
              attributes: true,
              attributeFilter: ["data-theme"],
              attributeOldValue: true,
            });
          };

          manager.syncRootTheme = () => {
            manager.drainAppearanceMutations(false);
            const appearance = manager.package.appearance;
            if (appearance !== "light" && appearance !== "dark") {
              manager.restoreRootTheme();
              return;
            }
            if (!manager.rootThemeOriginal) manager.rootThemeOriginal = manager.readRootTheme();
            manager.writeRootTheme({ present: true, value: appearance });
          };

          manager.restoreRootTheme = () => {
            if (!manager.rootThemeOriginal) return;
            const original = manager.rootThemeOriginal;
            manager.rootThemeOriginal = null;
            manager.writeRootTheme(original);
          };

          manager.theme = () => {
            const appearance = manager.package.appearance;
            if (appearance === "light" || appearance === "dark") return appearance;
            const root = document.documentElement;
            const declared = (root?.getAttribute("data-theme") || document.body?.getAttribute("data-theme") || "").toLowerCase();
            if (declared === "light" || declared === "dark") return declared;
            if (root?.classList.contains("dark") || document.body?.classList.contains("dark")) return "dark";
            return manager.appearanceMedia?.matches ? "dark" : "light";
          };

          manager.scene = () => {
            const visibleComposers = collect(document.documentElement, partSelectors.composer).filter(isVisible);
            if (visibleComposers.some((composer) => composer.getAttribute(codex.composerVariantAttribute) !== codex.composerHomeVariant)) return "conversation";
            if (visibleComposers.length > 0) return "home";
            const thread = document.querySelector(partSelectors.conversation);
            if (isVisible(thread)) return "conversation";
            const visibleMessage = Array.from(document.querySelectorAll(codex.messageRoleSelector))
              .some((message) => isVisible(message));
            if (visibleMessage) return "conversation";
            return "home";
          };

          manager.discover = (scope) => {
            if (!scope?.querySelectorAll) return;
            const candidates = new Map();
            const add = (part, elements) => {
              for (const element of elements) {
                if (isElement(element)) candidates.set(element, part);
              }
            };
            add("window", collect(scope, partSelectors.window));
            add("content", collect(scope, partSelectors.content));
            add("sidebar", collect(scope, partSelectors.sidebar));
            add("header", collect(scope, partSelectors.header));
            const homeAreas = collect(scope, codex.homeRootSelector)
              .filter((element) => element.querySelector(codex.homeSentinelSelector));
            if (isElement(scope)) {
              const activeHome = scope.closest(codex.homeRootSelector);
              if (activeHome?.querySelector(codex.homeSentinelSelector)) homeAreas.push(activeHome);
            }
            add("home", homeAreas);
            add("home.hero", collect(scope, partSelectors["home.hero"]));
            add("projectList", collect(scope, partSelectors.projectList));
            add("conversation", collect(scope, partSelectors.conversation));
            const userMessages = collect(scope, partSelectors["message.user"]);
            const assistantMessages = collect(scope, partSelectors["message.assistant"]);
            add("message.user", userMessages);
            add("message.assistant", assistantMessages);
            add("codeBlock", collect(scope, codex.codeBlockSelector).filter((pre) => !!pre.closest(codex.messageArticleSelector)));

            const composerOwners = collect(scope, partSelectors.composer);
            add("composer", composerOwners);
            add("composer.editor", collect(scope, partSelectors["composer.editor"])
              .filter((editor) => !!editor.closest(partSelectors.composer)));
            add("composer.toolbar", collect(scope, partSelectors["composer.toolbar"])
              .filter((toolbar) => !!toolbar.closest(partSelectors.composer)));
            add("button.primary", collect(scope, partSelectors["button.primary"]));
            add("button.secondary", collect(scope, partSelectors["button.secondary"]));
            add("dialog", collect(scope, partSelectors.dialog));

            for (const [element, part] of candidates) manager.mark(element, part);
          };

          manager.matchesPart = (element, part) => {
            if (!isElement(element)) return false;
            if (part === "home") return element.matches(codex.homeRootSelector) && !!element.querySelector(codex.homeSentinelSelector);
            if (part === "composer") return element.matches(partSelectors.composer);
            if (part === "composer.editor" || part === "composer.toolbar" || part === "codeBlock" || part === "dialog") {
              const selector = partSelectors[part];
              if (!element.matches(selector)) return false;
              if (part === "codeBlock") return !!element.closest(codex.messageArticleSelector);
              if (part === "composer.editor" || part === "composer.toolbar") return !!element.closest(partSelectors.composer);
              return true;
            }
            const selector = partSelectors[part];
            return !!selector && element.matches(selector);
          };

          manager.mark = (element, part) => {
            const oldPart = manager.parts.get(element);
            if (oldPart === part && element.getAttribute("data-skin-part") === part) {
              manager.rebindOwner(element);
              return;
            }
            if (oldPart && oldPart !== part) manager.releaseOwner(element);
            if (!manager.partOriginals.has(element)) {
              manager.partOriginals.set(element, element.hasAttribute("data-skin-part") ? element.getAttribute("data-skin-part") : null);
            }
            manager.parts.set(element, part);
            if (element.getAttribute("data-skin-part") !== part) element.setAttribute("data-skin-part", part);
            manager.bindOwner(element, part);
          };

          manager.surfaceFor = (owner, part) => {
            if (part === "composer") {
              if (owner.matches(codex.composerHomeVariantSelector)) return owner.querySelector(codex.composerBodySelector);
              return owner;
            }
            if (part === "message.user" || part === "message.assistant") return owner.firstElementChild;
            return owner;
          };

          manager.bindOwner = (owner, part) =>
            fills.bind(owner, part, manager.active?.fillOpacity?.[part]);

          manager.rebindOwner = (owner) => {
            const part = manager.parts.get(owner);
            if (!part) return;
            if (owner.isConnected) manager.bindOwner(owner, part);
            else fills.unbind(owner);
          };

          manager.rebindAll = () => {
            fills.reset();
            for (const [owner, part] of manager.parts) manager.bindOwner(owner, part);
          };

          manager.releaseOwner = (owner) => {
            fills.unbind(owner);
            manager.parts.delete(owner);
            const original = manager.partOriginals.get(owner);
            if (original === null) owner.removeAttribute("data-skin-part");
            else if (original !== undefined) owner.setAttribute("data-skin-part", original);
            manager.partOriginals.delete(owner);
          };

          manager.releaseSubtree = (scope) => {
            const marked = collect(scope, "[data-skin-part]");
            for (const element of marked) {
              if (manager.parts.has(element)) manager.releaseOwner(element);
            }
          };

          manager.syncImageUrls = () => {
            const wanted = new Set(Object.keys(manager.package.images || {}));
            for (const [key, url] of manager.blobUrls) {
              if (!wanted.has(key)) {
                URL.revokeObjectURL(url);
                manager.blobUrls.delete(key);
              }
            }
            for (const [key, dataUrl] of Object.entries(manager.package.images || {})) {
              if (manager.blobUrls.has(key)) continue;
              try {
                const comma = dataUrl.indexOf(",");
                if (comma < 0) continue;
                const mediaType = dataUrl.slice(5, dataUrl.indexOf(";", 5));
                const binary = atob(dataUrl.slice(comma + 1));
                const bytes = new Uint8Array(binary.length);
                for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
                const url = URL.createObjectURL(new Blob([bytes], { type: mediaType || "application/octet-stream" }));
                manager.blobUrls.set(key, url);
              } catch (_) {
                // Keep the data URL as the compatible fallback when Blob URLs are unavailable.
              }
            }
          };

          manager.imageUrl = (key) => manager.blobUrls.get(key) || manager.package.images?.[key] || "";

          manager.syncActiveStyle = () => {
            const css = manager.active?.css || "";
            if (!css) {
              manager.styleElement?.remove();
              manager.styleElement = null;
              return;
            }
            if (!manager.styleElement) {
              manager.styleElement = document.createElement("style");
              manager.styleElement.id = "skin-manager-runtime-style";
              manager.styleElement.setAttribute("data-skin-manager-owned", "true");
            }
            if (!manager.styleElement.isConnected) document.head.insertBefore(manager.styleElement, document.head.firstChild);
            if (manager.styleElement.textContent !== css) manager.styleElement.textContent = css;
          };

          manager.refreshActive = (force) => {
            if (!manager.started || manager.removed) return;
            manager.syncRootTheme();
            const theme = manager.theme();
            const scene = manager.scene();
            const key = `${theme}:${scene}`;
            if (!force && key === manager.activeKey) {
              manager.syncActiveStyle();
              return;
            }
            manager.activeKey = key;
            manager.active = manager.package.styles?.[theme]?.[scene] || null;
            manager.syncActiveStyle();
            background.sync(manager.active?.background);
            manager.rebindAll();
            if (manager.active) manager.installNativeCaptionBackground();
            else manager.disposeNativeCaptionBackground();
            manager.updateNativeCaptionBackground();
          };

          manager.remove = () => {
            if (manager.removed) return;
            manager.drainAppearanceMutations(false);
            manager.restoreRootTheme();
            manager.removed = true;
            manager.observer?.disconnect();
            manager.readyObserver?.disconnect();
            manager.appearanceObserver?.disconnect();
            manager.disposeNativeCaptionBackground();
            if (manager.readyHandler) document.removeEventListener("DOMContentLoaded", manager.readyHandler);
            if (manager.appearanceMedia && manager.appearanceHandler) manager.appearanceMedia.removeEventListener("change", manager.appearanceHandler);
            if (manager.reconcileFrame !== null) cancelAnimationFrame(manager.reconcileFrame);
            fills.reset();
            for (const [element] of manager.parts) {
              const original = manager.partOriginals.get(element);
              if (original === null) element.removeAttribute("data-skin-part");
              else if (original !== undefined) element.setAttribute("data-skin-part", original);
            }
            manager.parts.clear();
            manager.partOriginals.clear();
            manager.styleElement?.remove();
            background.remove();
            for (const url of manager.blobUrls.values()) URL.revokeObjectURL(url);
            manager.blobUrls.clear();
            if (window[managerKey] === manager) delete window[managerKey];
          };

          window[managerKey] = manager;
          manager.apply(runtimePackage);
        })()
        """;
}
