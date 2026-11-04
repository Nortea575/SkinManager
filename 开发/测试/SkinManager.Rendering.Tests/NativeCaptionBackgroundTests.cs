using System.Diagnostics;
using System.Text.Json;

internal static class NativeCaptionBackgroundTests
{
    public static void VerifyRuntime(
        string runtimeScript,
        string expectedForDarkSymbolsBackground,
        string expectedForLightSymbolsBackground,
        string? skinChangeRuntimeScript = null,
        string? expectedChangedForDarkSymbolsBackground = null,
        string? expectedChangedForLightSymbolsBackground = null,
        string? noCaptionRuntimeScript = null)
    {
        var harness = HarnessTemplate.Replace(
            "__RUNTIME_SOURCE__",
            JsonSerializer.Serialize(runtimeScript),
            StringComparison.Ordinal)
            .Replace("__EXPECTED_FOR_DARK_SYMBOLS_BACKGROUND__", JsonSerializer.Serialize(expectedForDarkSymbolsBackground), StringComparison.Ordinal)
            .Replace("__EXPECTED_FOR_LIGHT_SYMBOLS_BACKGROUND__", JsonSerializer.Serialize(expectedForLightSymbolsBackground), StringComparison.Ordinal)
            .Replace("__SKIN_CHANGE_RUNTIME_SOURCE__", JsonSerializer.Serialize(skinChangeRuntimeScript), StringComparison.Ordinal)
            .Replace("__EXPECTED_CHANGED_FOR_DARK_SYMBOLS_BACKGROUND__", JsonSerializer.Serialize(expectedChangedForDarkSymbolsBackground), StringComparison.Ordinal)
            .Replace("__EXPECTED_CHANGED_FOR_LIGHT_SYMBOLS_BACKGROUND__", JsonSerializer.Serialize(expectedChangedForLightSymbolsBackground), StringComparison.Ordinal)
            .Replace("__NO_CAPTION_RUNTIME_SOURCE__", JsonSerializer.Serialize(noCaptionRuntimeScript), StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), $"skin-manager-caption-{Guid.NewGuid():N}.js");
        File.WriteAllText(path, harness);
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
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Node for the offline caption runtime check.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The offline caption runtime check failed: {standardOutput}{standardError}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string HarnessTemplate = """
        const assert = require("node:assert/strict");
        const vm = require("node:vm");
        const runtimeSource = __RUNTIME_SOURCE__;
        const expectedForDarkSymbolsBackground = __EXPECTED_FOR_DARK_SYMBOLS_BACKGROUND__;
        const expectedForLightSymbolsBackground = __EXPECTED_FOR_LIGHT_SYMBOLS_BACKGROUND__;
        const skinChangeRuntimeSource = __SKIN_CHANGE_RUNTIME_SOURCE__;
        const expectedChangedForDarkSymbolsBackground = __EXPECTED_CHANGED_FOR_DARK_SYMBOLS_BACKGROUND__;
        const expectedChangedForLightSymbolsBackground = __EXPECTED_CHANGED_FOR_LIGHT_SYMBOLS_BACKGROUND__;
        const noCaptionRuntimeSource = __NO_CAPTION_RUNTIME_SOURCE__;

        class FakeElement {
          constructor(tagName) {
            this.tagName = tagName;
            this.nodeType = 1;
            this.attributes = new Map();
            this.children = [];
            this.style = {};
            this.parentElement = null;
            this.isConnected = true;
            this.className = "";
            this.classList = { contains: () => false };
          }
          setAttribute(name, value) { this.attributes.set(name, String(value)); }
          getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
          hasAttribute(name) { return this.attributes.has(name); }
          removeAttribute(name) { this.attributes.delete(name); }
          matches() { return false; }
          querySelectorAll() { return []; }
          querySelector() { return null; }
          closest() { return null; }
          contains(element) { return this.children.includes(element); }
          appendChild(element) {
            if (element.parentElement) element.remove();
            element.parentElement = this;
            element.isConnected = true;
            this.children.push(element);
            return element;
          }
          insertBefore(element, before) {
            if (element.parentElement) element.remove();
            element.parentElement = this;
            element.isConnected = true;
            const index = this.children.indexOf(before);
            this.children.splice(index < 0 ? this.children.length : index, 0, element);
            return element;
          }
          prepend(element) { this.insertBefore(element, this.children[0] || null); }
          remove() {
            if (this.parentElement) {
              const index = this.parentElement.children.indexOf(this);
              if (index >= 0) this.parentElement.children.splice(index, 1);
            }
            this.parentElement = null;
            this.isConnected = false;
          }
        }

        class FakeMutationObserver {
          constructor(callback) { this.callback = callback; }
          observe() {}
          disconnect() {}
          takeRecords() { return []; }
        }

        function createEnvironment({ withOverlay = true, withBridge = true, withShell = true } = {}) {
          const root = new FakeElement("html");
          const head = new FakeElement("head");
          const body = new FakeElement("body");
          const shell = new FakeElement("main");
          shell.setAttribute("data-app-shell-frame", "true");
          const overlayListeners = new Map();
          const windowListeners = new Map();
          const themeSubscribers = new Set();
          let themeUnsubscribeCount = 0;
          let themeVariant = "light";
          let rect = { x: 0, y: 0, width: 960, height: 36 };

          const overlay = {
            visible: true,
            getTitlebarAreaRect: () => ({ ...rect }),
            addEventListener(name, handler) {
              if (!overlayListeners.has(name)) overlayListeners.set(name, new Set());
              overlayListeners.get(name).add(handler);
            },
            removeEventListener(name, handler) { overlayListeners.get(name)?.delete(handler); },
            dispatch(name) { for (const handler of [...(overlayListeners.get(name) || [])]) handler(); },
            listenerCount(name) { return overlayListeners.get(name)?.size || 0; },
            setRect(value) { rect = { ...value }; },
          };

          const bridge = {
            getSystemThemeVariant: () => themeVariant,
            subscribeToSystemThemeVariant(handler) {
              themeSubscribers.add(handler);
              return () => {
                if (themeSubscribers.delete(handler)) themeUnsubscribeCount++;
              };
            },
            setTheme(value) {
              themeVariant = value;
              for (const handler of [...themeSubscribers]) handler(value);
            },
          };

          const document = {
            documentElement: root,
            head,
            body,
            querySelector(selector) {
              return selector === "[data-app-shell-frame]" && withShell ? shell : null;
            },
            querySelectorAll() { return []; },
            createElement: (tagName) => new FakeElement(tagName),
            addEventListener() {},
            removeEventListener() {},
          };
          const window = {
            innerWidth: 1000,
            electronBridge: withBridge ? bridge : undefined,
            addEventListener(name, handler) {
              if (!windowListeners.has(name)) windowListeners.set(name, new Set());
              windowListeners.get(name).add(handler);
            },
            removeEventListener(name, handler) { windowListeners.get(name)?.delete(handler); },
            dispatch(name) { for (const handler of [...(windowListeners.get(name) || [])]) handler(); },
            listenerCount(name) { return windowListeners.get(name)?.size || 0; },
            matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
          };
          const navigator = withOverlay ? { windowControlsOverlay: overlay } : {};
          const context = vm.createContext({
            window,
            document,
            navigator,
            MutationObserver: FakeMutationObserver,
            requestAnimationFrame: () => 1,
            cancelAnimationFrame() {},
            getComputedStyle: () => ({ display: "block", visibility: "visible", getPropertyValue: () => "" }),
          });
          const captionElement = () => body.children.find((element) => element.getAttribute("data-skin-manager-caption-background") === "true") || null;
          return {
            context, window, document, overlay, bridge, themeSubscribers,
            get themeUnsubscribeCount() { return themeUnsubscribeCount; },
            captionElement,
          };
        }

        const app = createEnvironment();
        let oldRuntimeWasRemoved = false;
        app.window.__SKIN_MANAGER_RUNTIME__ = {
          appearanceVersion: 3,
          remove() { oldRuntimeWasRemoved = true; },
        };
        vm.runInContext(runtimeSource, app.context);
        assert.equal(oldRuntimeWasRemoved, true, "The new runtime removes a version 2 manager before starting.");
        assert.equal(app.window.__SKIN_MANAGER_RUNTIME__.appearanceVersion, 4);
        let caption = app.captionElement();
        assert.ok(caption, "A skin gets a local caption background in an app window with native controls.");
        assert.equal(caption.style.left, "960px");
        assert.equal(caption.style.width, "40px");
        assert.equal(caption.style.height, "36px");
        assert.equal(caption.style.backgroundColor, expectedForDarkSymbolsBackground, "Dark native symbols use their precomputed skin surface color in a light host theme.");
        assert.equal(caption.style.pointerEvents, "none");
        assert.equal(app.overlay.listenerCount("geometrychange"), 1);
        assert.equal(app.window.listenerCount("resize"), 1);
        assert.equal(app.themeSubscribers.size, 1);

        app.bridge.setTheme("dark");
        assert.equal(caption.style.backgroundColor, expectedForLightSymbolsBackground, "Light native symbols use their precomputed skin accent or surface color in a dark host theme.");

        app.bridge.setTheme("unknown");
        assert.equal(app.captionElement(), null, "An unknown native theme removes the backplate instead of retaining a stale color.");
        app.bridge.setTheme("dark");
        caption = app.captionElement();
        assert.ok(caption, "A later valid native theme restores the backplate.");

        if (skinChangeRuntimeSource) {
          vm.runInContext(skinChangeRuntimeSource, app.context);
          caption = app.captionElement();
          assert.ok(caption, "Applying a new skin keeps the measured backplate available.");
          assert.equal(caption.style.backgroundColor, expectedChangedForLightSymbolsBackground, "A skin hot update replaces the light-glyph contrast surface.");
          app.bridge.setTheme("light");
          assert.equal(app.captionElement()?.style.backgroundColor, expectedChangedForDarkSymbolsBackground, "A skin hot update also replaces the dark-glyph contrast surface.");
          app.bridge.setTheme("dark");
          assert.equal(app.captionElement()?.style.backgroundColor, expectedChangedForLightSymbolsBackground, "Native theme changes continue to select the new skin's plan.");
        }

        app.overlay.setRect({ x: 0, y: 0, width: 980, height: 36 });
        app.overlay.dispatch("geometrychange");
        assert.equal(caption.style.left, "980px", "The right edge follows the measured titlebar area.");
        assert.equal(caption.style.width, "20px");
        app.window.innerWidth = 1020;
        app.window.dispatch("resize");
        assert.equal(caption.style.width, "40px", "Resize recomputes the right controls region from measured geometry.");

        vm.runInContext(runtimeSource, app.context);
        assert.equal(app.overlay.listenerCount("geometrychange"), 1, "Reusing the runtime does not duplicate overlay listeners.");
        assert.equal(app.window.listenerCount("resize"), 1, "Reusing the runtime does not duplicate resize listeners.");
        assert.equal(app.themeSubscribers.size, 1, "Reusing the runtime does not duplicate native-theme subscriptions.");

        app.overlay.visible = false;
        app.overlay.dispatch("geometrychange");
        assert.equal(app.captionElement(), null, "An invisible native controls overlay removes the backdrop.");
        app.overlay.visible = true;
        app.overlay.setRect({ x: 0, y: 0, width: 1020, height: 36 });
        app.overlay.dispatch("geometrychange");
        assert.equal(app.captionElement(), null, "A zero-width native controls region stays hidden.");

        app.overlay.setRect({ x: 0, y: 0, width: 980, height: 36 });
        app.overlay.dispatch("geometrychange");
        caption = app.captionElement();
        assert.ok(caption, "A newly available measured region restores the backdrop.");
        app.window.__SKIN_MANAGER_RUNTIME__.remove();
        assert.equal(app.captionElement(), null, "Runtime removal deletes the manager-owned backdrop.");
        assert.equal(app.overlay.listenerCount("geometrychange"), 0, "Runtime removal detaches the geometry listener.");
        assert.equal(app.window.listenerCount("resize"), 0, "Runtime removal detaches the resize listener.");
        assert.equal(app.themeSubscribers.size, 0, "Runtime removal unsubscribes from native theme updates.");
        assert.equal(app.themeUnsubscribeCount, 1);

        const preview = createEnvironment({ withOverlay: false });
        vm.runInContext(runtimeSource, preview.context);
        assert.equal(preview.captionElement(), null, "A preview without native window controls gets no caption backdrop.");
        assert.equal(preview.themeSubscribers.size, 0, "A preview without native controls does not subscribe to native theme updates.");

        const untrusted = createEnvironment({ withShell: false });
        vm.runInContext(runtimeSource, untrusted.context);
        assert.equal(untrusted.captionElement(), null, "The caption backdrop is restricted to the app shell.");
        if (noCaptionRuntimeSource) {
          const noCaption = createEnvironment();
          vm.runInContext(noCaptionRuntimeSource, noCaption.context);
          assert.equal(noCaption.captionElement(), null, "An inactive native skin package leaves the caption controls untouched.");
          noCaption.window.__SKIN_MANAGER_RUNTIME__?.remove?.();
        }
        app.window.__SKIN_MANAGER_RUNTIME__?.remove?.();
        untrusted.window.__SKIN_MANAGER_RUNTIME__?.remove?.();
        preview.window.__SKIN_MANAGER_RUNTIME__?.remove?.();
        """;
}
