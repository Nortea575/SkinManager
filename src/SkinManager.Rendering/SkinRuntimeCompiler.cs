using System.Security.Cryptography;
using System.Text.Json;
using SkinManager.Core;

namespace SkinManager.Rendering;

public static class SkinRuntimeCompiler
{
    private static readonly JsonSerializerOptions RuntimeJsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildRuntimeScript(SkinPackage package, double? userTransparency = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (userTransparency.HasValue && (!double.IsFinite(userTransparency.Value) || userTransparency.Value is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(userTransparency), "透明度必须介于 0 到 100 之间。");
        }

        var panelOpacity = userTransparency.HasValue ? (100 - userTransparency.Value) / 100 : (double?)null;
        var imageData = new Dictionary<string, string>(StringComparer.Ordinal);
        var styles = new Dictionary<string, IReadOnlyDictionary<string, NativeSkinStylePayload>>(StringComparer.Ordinal);

        foreach (var appearance in new[] { SkinAppearance.Light, SkinAppearance.Dark })
        {
            var scenes = new Dictionary<string, NativeSkinStylePayload>(StringComparer.Ordinal);
            foreach (var scene in new[] { SkinScene.Home, SkinScene.Conversation })
            {
                var resolved = SkinResolver.Resolve(package, appearance, scene, panelOpacity);
                string? imageKey = null;
                if (resolved.Background is { ImagePath: { } imagePath } background)
                {
                    var bytes = background.ImageBytes ?? (package.Files.TryGetValue(imagePath, out var packageBytes) ? packageBytes : null);
                    if (bytes is null || bytes.Length == 0)
                    {
                        throw new InvalidDataException($"皮肤背景资源“{imagePath}”没有可用的图片数据。");
                    }

                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    imageKey = "sha256-" + hash;
                    imageData.TryAdd(imageKey, $"data:{GetImageMediaType(imagePath)};base64,{Convert.ToBase64String(bytes)}");
                }

                scenes[scene == SkinScene.Home ? "home" : "conversation"] = NativeSkinStyleBuilder.Build(resolved, imageKey);
            }

            styles[appearance == SkinAppearance.Light ? "light" : "dark"] = scenes;
        }

        var hasOverrides = package.Appearance != SkinAppearance.System || styles.Values
            .SelectMany(sceneStyles => sceneStyles.Values)
            .Any(payload => payload.Css.Length > 0 || payload.FillOpacity.Count > 0 || payload.Background is not null);
        var runtime = new RuntimePackage(hasOverrides, package.Appearance.ToString().ToLowerInvariant(), styles, imageData);
        var json = JsonSerializer.Serialize(runtime, RuntimeJsonOptions);
        return RuntimeScriptTemplate
            .Replace("__CODEX_ADAPTER_JSON__", CodexAdapter.RuntimeContractJson, StringComparison.Ordinal)
            .Replace("__SKIN_FILL_ENGINE_SOURCE__", SkinFillEngine.Source, StringComparison.Ordinal)
            .Replace("__SKIN_RUNTIME_PACKAGE_JSON__", json, StringComparison.Ordinal);
    }

    public static string BuildRemoveScript() => "(() => { window.__SKIN_MANAGER_RUNTIME__?.remove?.(); })();";

    private static string GetImageMediaType(string imagePath) => Path.GetExtension(imagePath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

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
          if (!runtimePackage.enabled) {
            existing?.remove?.();
            return;
          }
          if (existing) {
            if (existing.appearanceVersion === 4 && typeof existing.apply === "function") {
              existing.apply(runtimePackage);
              return;
            }
            existing.remove?.();
          }

          const fillEvents = ["pointerover", "pointerout", "pointerdown", "pointerup", "focus", "blur", "focusin", "focusout"];
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
            backgroundElement: null,
            captionBackgroundElement: null,
            nativeThemeVariant: null,
            nativeThemeUnsubscribe: null,
            windowControlsOverlay: null,
            windowControlsGeometryHandler: null,
            windowResizeHandler: null,
            rootBackgroundOriginal: null,
            parts: new Map(),
            partOriginals: new Map(),
            surfaces: new Set(),
            bindings: new WeakMap(),
            fillOriginals: new WeakMap(),
            fillApplied: new WeakMap(),
            ownerSurfaces: new Map(),
            ownerHandlers: new Map(),
            ownerFrames: new Map(),
            nativeBackgroundCache: new Map(),
            blobUrls: new Map(),
            appearanceVersion: 4,
          };

          const fillEngine = createSkinFillEngine({
            document,
            getComputedStyle: (element) => getComputedStyle(element),
            root: document.documentElement,
            statePriority: codex.statePriority,
            stateSelectors: codex.stateSelectors,
            fillOriginals: manager.fillOriginals,
            fillApplied: manager.fillApplied,
            nativeBackgroundCache: manager.nativeBackgroundCache,
            isBackgroundActive: () => document.documentElement.getAttribute("data-skin-background-active") === "true",
          });
          manager.currentState = fillEngine.currentState;
          manager.matchesState = fillEngine.matchesState;
          manager.readBackgroundColor = fillEngine.readInlineFill;
          manager.sameInlineFill = fillEngine.sameInlineFill;
          manager.isAppliedFill = fillEngine.isAppliedFill;
          manager.restoreFill = fillEngine.restoreFill;
          manager.parseColor = fillEngine.parseColor;
          manager.nativeBackground = fillEngine.nativeBackground;
          manager.applyFill = fillEngine.applyFill;

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
                  const binding = manager.bindings.get(target);
                  if (!manager.isAppliedFill(target)) {
                    if (binding) manager.scheduleOwnerFill(binding.owner);
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
                const binding = manager.bindings.get(target);
                if (binding) manager.scheduleOwnerFill(binding.owner);
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
              if (manager.active?.background) manager.updateBackground();
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

          manager.bindOwner = (owner, part) => {
            const config = manager.active?.fillOpacity?.[part];
            if (!config) return;
            const surface = manager.surfaceFor(owner, part);
            if (!surface) return;
            let surfaces = manager.ownerSurfaces.get(owner);
            if (!surfaces) {
              surfaces = new Set();
              manager.ownerSurfaces.set(owner, surfaces);
            }
            surfaces.add(surface);
            manager.surfaces.add(surface);
            manager.bindings.set(surface, { owner, part });
            manager.applyFill(surface, owner, config);
            if (manager.hasStatefulFill(config)) manager.attachOwner(owner);
          };

          manager.rebindOwner = (owner) => {
            const part = manager.parts.get(owner);
            if (!part) return;
            manager.unbindOwner(owner);
            if (owner.isConnected) manager.bindOwner(owner, part);
          };

          manager.hasStatefulFill = (config) => Object.keys(config.states || {}).length > 0
            || Object.keys(config.noFillStates || {}).length > 0
            || Object.keys(config.backgroundStates || {}).length > 0;

          manager.attachOwner = (owner) => {
            if (manager.ownerHandlers.has(owner)) return;
            const handler = () => manager.scheduleOwnerFill(owner);
            for (const eventName of fillEvents) owner.addEventListener(eventName, handler, true);
            manager.ownerHandlers.set(owner, handler);
          };

          manager.detachOwner = (owner) => {
            const handler = manager.ownerHandlers.get(owner);
            if (handler) {
              for (const eventName of fillEvents) owner.removeEventListener(eventName, handler, true);
              manager.ownerHandlers.delete(owner);
            }
            const frame = manager.ownerFrames.get(owner);
            if (frame !== undefined) cancelAnimationFrame(frame);
            manager.ownerFrames.delete(owner);
            manager.ownerSurfaces.delete(owner);
          };

          manager.scheduleOwnerFill = (owner) => {
            if (manager.ownerFrames.has(owner)) return;
            const frame = requestAnimationFrame(() => {
              manager.ownerFrames.delete(owner);
              const surfaces = manager.ownerSurfaces.get(owner);
              if (!surfaces) return;
              for (const surface of surfaces) {
                const binding = manager.bindings.get(surface);
                const config = binding && manager.active?.fillOpacity?.[binding.part];
                if (binding && config) manager.applyFill(surface, owner, config);
              }
            });
            manager.ownerFrames.set(owner, frame);
          };

          manager.unbindOwner = (owner) => {
            const surfaces = manager.ownerSurfaces.get(owner);
            if (surfaces) {
              for (const surface of surfaces) {
                manager.restoreFill(surface);
                manager.surfaces.delete(surface);
                manager.bindings.delete(surface);
              }
            }
            manager.detachOwner(owner);
          };

          manager.rebindAll = () => {
            for (const owner of Array.from(manager.ownerSurfaces.keys())) manager.unbindOwner(owner);
            for (const [owner, part] of manager.parts) manager.bindOwner(owner, part);
          };

          manager.releaseOwner = (owner) => {
            manager.unbindOwner(owner);
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

          manager.setBackgroundActive = (active) => {
            const root = document.documentElement;
            if (!manager.rootBackgroundOriginal) {
              manager.rootBackgroundOriginal = {
                present: root.hasAttribute("data-skin-background-active"),
                value: root.getAttribute("data-skin-background-active"),
              };
            }
            if (active) root.setAttribute("data-skin-background-active", "true");
            else root.removeAttribute("data-skin-background-active");
          };

          manager.updateBackground = () => {
            const config = manager.active?.background;
            let background = manager.backgroundElement;
            if (!config) {
              background?.remove();
              manager.backgroundElement = null;
              manager.setBackgroundActive(false);
              return;
            }
            if (!background) {
              background = document.createElement("div");
              background.className = "skin-manager-runtime-background";
              background.setAttribute("data-skin-manager-owned", "true");
              background.setAttribute("aria-hidden", "true");
              Object.assign(background.style, {
                position: "fixed", inset: "0", zIndex: "0", pointerEvents: "none", overflow: "hidden",
              });
              manager.backgroundElement = background;
            }
            const shell = document.querySelector(partSelectors.window);
            if (shell?.parentElement === document.body) {
              if (background.parentElement !== document.body || background.nextSibling !== shell) document.body.insertBefore(background, shell);
            } else if (background.parentElement !== document.body) {
              document.body.prepend(background);
            }
            manager.setBackgroundActive(true);
            const computedRoot = getComputedStyle(document.documentElement);
            const fallback = codex.nativeBackgroundFallbackVariables.map((variable) => computedRoot.getPropertyValue(variable).trim()).find(Boolean)
              || getComputedStyle(document.body).backgroundColor;
            background.style.backgroundColor = config.color || fallback;

            let image = background.querySelector(".skin-manager-image");
            const imageUrl = config.imageUrl ? manager.imageUrl(config.imageUrl) : "";
            if (imageUrl) {
              if (!image) {
                image = document.createElement("div");
                image.className = "skin-manager-image";
                image.setAttribute("data-skin-manager-owned", "true");
                Object.assign(image.style, { position: "absolute", inset: "0", backgroundRepeat: "no-repeat" });
                background.appendChild(image);
              }
              image.style.backgroundImage = `url(${JSON.stringify(imageUrl)})`;
              image.style.backgroundSize = config.fit || "";
              image.style.backgroundPosition = config.focusX !== null && config.focusY !== null
                ? `${config.focusX * 100}% ${config.focusY * 100}%`
                : config.focusX !== null ? `${config.focusX * 100}%`
                : config.focusY !== null ? `center ${config.focusY * 100}%` : "";
              image.style.opacity = config.opacity ?? "";
            } else {
              image?.remove();
            }

            let overlay = background.querySelector(".skin-manager-overlay");
            if (config.overlayColor) {
              if (!overlay) {
                overlay = document.createElement("div");
                overlay.className = "skin-manager-overlay";
                overlay.setAttribute("data-skin-manager-owned", "true");
                Object.assign(overlay.style, { position: "absolute", inset: "0" });
                background.appendChild(overlay);
              }
              overlay.style.backgroundColor = config.overlayColor;
              overlay.style.opacity = config.overlayOpacity ?? "";
            } else {
              overlay?.remove();
            }
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
            manager.nativeBackgroundCache.clear();
            manager.syncActiveStyle();
            manager.setBackgroundActive(!!manager.active?.background);
            manager.updateBackground();
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
            for (const owner of Array.from(manager.ownerSurfaces.keys())) manager.unbindOwner(owner);
            for (const [element] of manager.parts) {
              const original = manager.partOriginals.get(element);
              if (original === null) element.removeAttribute("data-skin-part");
              else if (original !== undefined) element.setAttribute("data-skin-part", original);
            }
            manager.parts.clear();
            manager.partOriginals.clear();
            manager.styleElement?.remove();
            manager.backgroundElement?.remove();
            if (manager.rootBackgroundOriginal && document.documentElement) {
              if (manager.rootBackgroundOriginal.present) document.documentElement.setAttribute("data-skin-background-active", manager.rootBackgroundOriginal.value ?? "");
              else document.documentElement.removeAttribute("data-skin-background-active");
            }
            for (const url of manager.blobUrls.values()) URL.revokeObjectURL(url);
            manager.blobUrls.clear();
            manager.nativeBackgroundCache.clear();
            if (window[managerKey] === manager) delete window[managerKey];
          };

          window[managerKey] = manager;
          manager.apply(runtimePackage);
        })()
        """;
}
