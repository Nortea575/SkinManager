using System.Diagnostics;
using System.Reflection;
using SkinManager.Rendering;

internal static class SharedFillEngineTests
{
    public static void Verify(string runtimeScript, string previewScript)
    {
        var assembly = typeof(NativeSkinStyleBuilder).Assembly;
        using var stream = assembly.GetManifestResourceStream("SkinManager.Rendering.SkinFillEngine.js")
            ?? throw new InvalidOperationException("Missing embedded shared fill engine.");
        using var reader = new StreamReader(stream);
        var engineSource = reader.ReadToEnd();

        Require(runtimeScript.Contains(engineSource, StringComparison.Ordinal), "Runtime embeds the shared fill engine source.");
        Require(previewScript.Contains(engineSource, StringComparison.Ordinal), "Preview embeds the shared fill engine source.");
        Require(runtimeScript.Contains("const getStyle = (element) => getComputedStyle(element)", StringComparison.Ordinal) &&
                previewScript.Contains("const getStyle = (element) => getComputedStyle(element)", StringComparison.Ordinal),
            "Both hosts preserve the window receiver when supplying getComputedStyle.");
        Require(runtimeScript.Contains("owner.firstElementChild", StringComparison.Ordinal) &&
                previewScript.Contains("owner.querySelector(\":scope > div\")", StringComparison.Ordinal),
            "Runtime and preview retain their distinct native message surface targets.");

        var harness = engineSource + "\n" + Harness;
        var path = Path.Combine(Path.GetTempPath(), $"skin-manager-fill-engine-{Guid.NewGuid():N}.js");
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
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Node for fill engine behavior checks.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Shared fill engine behavior checks failed: {output}{error}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string Harness = """
        const assert = require("node:assert/strict");
        class FakeStyle {
          constructor(element) { this.element = element; this.values = new Map(); this.priorities = new Map(); }
          get backgroundColor() { return this.getPropertyValue("background-color"); }
          set backgroundColor(value) { this.setProperty("background-color", value); }
          getPropertyValue(name) { return this.values.get(name) || ""; }
          getPropertyPriority(name) { return this.priorities.get(name) || ""; }
          setProperty(name, value, priority = "") {
            this.values.set(name, String(value));
            if (priority) this.priorities.set(name, priority); else this.priorities.delete(name);
          }
          removeProperty(name) { this.values.delete(name); this.priorities.delete(name); }
        }
        class FakeElement {
          constructor(background = "") {
            this.style = new FakeStyle(this);
            this.attributes = new Map();
            this.states = new Set();
            this.listeners = new Map();
            this.children = [];
            this.parentElement = null;
            this.computedBackground = background;
            this.removed = false;
            if (background) this.style.setProperty("background-color", background);
          }
          matches(selector) { return this.states.has(selector); }
          getAttribute(name) {
            if (name === "style") {
              const value = this.style.getPropertyValue("background-color");
              if (!value) return null;
              const priority = this.style.getPropertyPriority("background-color");
              return `background-color: ${value}${priority ? ` !${priority}` : ""};`;
            }
            return this.attributes.get(name) ?? null;
          }
          setAttribute(name, value) { this.attributes.set(name, String(value)); }
          removeAttribute(name) { this.attributes.delete(name); }
          addEventListener(name, handler) { this.listeners.set(name, handler); }
          removeEventListener(name, handler) { if (this.listeners.get(name) === handler) this.listeners.delete(name); }
          dispatch(name) { this.listeners.get(name)?.(); }
          get nextSibling() {
            const siblings = this.parentElement?.children || [];
            return siblings[siblings.indexOf(this) + 1] || null;
          }
          querySelector(selector) { return this.children.find((element) => '.' + element.className === selector) || null; }
          insertBefore(element, before) {
            element.remove();
            const index = before ? this.children.indexOf(before) : this.children.length;
            this.children.splice(index, 0, element);
            element.parentElement = this;
            element.removed = false;
          }
          appendChild(element) { this.insertBefore(element, null); }
          prepend(element) { this.insertBefore(element, this.children[0] || null); }
          remove() {
            if (this.parentElement) this.parentElement.children.splice(this.parentElement.children.indexOf(this), 1);
            this.parentElement = null;
            this.removed = true;
          }
        }
        let probeCount = 0;
        const document = {
          body: { appendChild() { probeCount++; } },
          createElement(name) {
            assert.equal(name, "div");
            return new FakeElement();
          }
        };
        const host = {
          document,
          getComputedStyle(element) {
            const inline = element.style.getPropertyValue("background-color");
            if (inline.startsWith("var(")) return { backgroundColor: "rgb(7, 8, 9)" };
            return { backgroundColor: inline || element.computedBackground };
          },
          statePriority: ["disabled", "pressed", "focused", "hover", "selected"],
          stateSelectors: {
            disabled: "[data-disabled]", pressed: "[data-pressed]", focused: "[data-focused]",
            hover: "[data-hover]", selected: "[data-selected]"
          },
          fillOriginals: new WeakMap(), fillApplied: new WeakMap(),
          nativeBackgroundCache: new Map(), isBackgroundActive: () => false
        };
        const engine = createSkinFillEngine(host);
        const owner = new FakeElement();
        owner.states.add("[data-disabled]");
        owner.states.add("[data-hover]");
        const stateConfig = { states: { hover: 0.2, disabled: 0.7 } };
        assert.equal(engine.currentState(owner, stateConfig.states), "disabled", "runtime state priority keeps disabled above hover");

        const changedByHost = new FakeElement("rgb(10, 20, 30)");
        engine.applyFill(changedByHost, owner, { default: 0.25, states: stateConfig.states });
        assert.equal(changedByHost.style.getPropertyValue("background-color"), "rgba(10, 20, 30, 0.7)");
        assert.equal(changedByHost.style.getPropertyPriority("background-color"), "important");
        assert.equal(engine.isAppliedFill(changedByHost), true);
        changedByHost.style.setProperty("background-color", "rgb(40, 50, 60)");
        assert.equal(engine.isAppliedFill(changedByHost), false);
        engine.restoreFill(changedByHost);
        assert.equal(changedByHost.style.getPropertyValue("background-color"), "rgb(40, 50, 60)", "restore keeps a host style written after the skin fill");
        assert.equal(changedByHost.style.getPropertyPriority("background-color"), "");

        owner.states.clear();
        const restored = new FakeElement("rgb(1, 2, 3)");
        engine.applyFill(restored, owner, { default: 0.25, states: {} });
        engine.restoreFill(restored);
        assert.equal(restored.style.getPropertyValue("background-color"), "rgb(1, 2, 3)", "unload restores the original inline fill when the host did not change it");

        owner.states.add("[data-disabled]");
        const disabledSurface = new FakeElement("rgb(4, 5, 6)");
        engine.applyFill(disabledSurface, owner, {
          default: 0.25, states: { hover: 0.4 }, backgroundStates: { disabled: true }, noFillStates: { disabled: true }
        });
        assert.equal(disabledSurface.style.getPropertyValue("background-color"), "rgb(4, 5, 6)", "disabled background-state ownership leaves native fill intact");

        assert.deepEqual(engine.nativeBackground("--native-surface"), [7, 8, 9, 1]);
        assert.deepEqual(engine.nativeBackground("--native-surface"), [7, 8, 9, 1]);
        assert.equal(probeCount, 1, "native variable sampling is cached");
        assert.deepEqual(engine.parseColor("rgba(9, 8, 7, 0)"), [9, 8, 7, 0], "alpha zero stays transparent");

        const frames = new Map();
        let frameNumber = 0;
        const requestAnimationFrame = (callback) => { frames.set(++frameNumber, callback); return frameNumber; };
        const cancelAnimationFrame = (frame) => frames.delete(frame);
        const flushFrames = () => {
          for (const [frame, callback] of frames) {
            frames.delete(frame);
            callback();
          }
        };
        const stateOwner = new FakeElement();
        const stateSurface = new FakeElement("rgb(10, 20, 30)");
        let matchedSurface = stateSurface;
        const fills = createSkinFillBindings({ ...host, codex: host, surfaceFor: () => matchedSurface });
        fills.bind(stateOwner, "composer", {
          default: 0.3, states: { focused: 0.5 }, backgroundStates: { disabled: true }, noFillStates: { disabled: true }
        });
        assert.equal(fills.bindings.get(stateSurface).owner, stateOwner);
        assert.equal(stateSurface.style.backgroundColor, "rgba(10, 20, 30, 0.3)");
        stateOwner.states.add("[data-focused]");
        stateOwner.dispatch("focusin");
        stateOwner.dispatch("pointerover");
        assert.equal(frames.size, 1, "state changes for an owner share one pending update");
        flushFrames();
        assert.equal(stateSurface.style.backgroundColor, "rgba(10, 20, 30, 0.5)");

        stateOwner.states.add("[data-disabled]");
        fills.scheduleOwnerFill(stateOwner);
        flushFrames();
        assert.equal(stateSurface.style.backgroundColor, "rgb(10, 20, 30)");
        stateSurface.style.backgroundColor = "rgb(15, 25, 35)";
        stateOwner.states.clear();
        stateOwner.dispatch("focusout");
        flushFrames();
        assert.equal(stateSurface.style.backgroundColor, "rgba(15, 25, 35, 0.3)", "the host can change a native fill while the skin is not overriding it");

        stateOwner.dispatch("pointerout");
        matchedSurface = new FakeElement("rgb(30, 40, 50)");
        fills.bind(stateOwner, "composer", { default: 0.7, states: { focused: 0.3 } });
        assert.equal(frames.size, 0, "replacing an owner's surface cancels its previous update");
        assert.equal(fills.bindings.has(stateSurface), false);
        assert.equal(stateSurface.style.backgroundColor, "rgb(15, 25, 35)", "cleanup restores the last native fill");
        assert.equal(matchedSurface.style.backgroundColor, "rgba(30, 40, 50, 0.7)");

        stateOwner.dispatch("pointerout");
        fills.reset();
        assert.equal(frames.size, 0);
        assert.equal(stateOwner.listeners.size, 0);
        assert.equal(fills.bindings.size, 0);
        assert.equal(matchedSurface.style.backgroundColor, "rgb(30, 40, 50)");

        const root = new FakeElement();
        const body = new FakeElement();
        const shell = new FakeElement();
        body.appendChild(shell);
        root.setAttribute("data-skin-background-active", "native-marker");
        const backdrop = createSkinBackground({
          document: { documentElement: root, body, createElement: () => new FakeElement(), querySelector: () => shell },
          codex: { partSelectors: { window: "main" }, nativeBackgroundFallbackVariables: ["--native"] },
          getComputedStyle: () => ({ getPropertyValue: () => "rgb(1, 2, 3)" }),
          className: "skin-manager-runtime-background", resolveImageUrl: (key) => "blob:" + key,
        });
        const backgroundConfig = { overlayColor: "rgba(4, 5, 6, 0.5)", overlayOpacity: 0.6 };
        backdrop.sync(backgroundConfig);
        const background = body.children[0];
        const overlay = background.children[0];
        assert.equal(background.nextSibling, shell);
        assert.equal(background.getAttribute("data-skin-manager-owned"), "true");
        assert.equal(background.style.backgroundColor, "rgb(1, 2, 3)");
        assert.equal(backdrop.isActive(), true);
        backdrop.sync({ ...backgroundConfig, imageUrl: "gif", fit: "cover", focusX: 0.2, focusY: 0.8, opacity: 0.3 });
        const image = background.children[0];
        assert.equal(image.nextSibling, overlay, "a new image stays underneath an existing overlay");
        assert.equal(image.style.backgroundImage, 'url("blob:gif")');
        assert.equal(image.style.backgroundPosition, "20% 80%");
        backdrop.sync({ ...backgroundConfig, imageUrl: "gif", fit: "contain", opacity: 0.7 });
        assert.equal(body.children[0], background, "background reuses the same DOM node");
        assert.equal(background.children[0], image, "updating opacity and fit retains the animated image node");
        assert.equal(image.style.opacity, 0.7);
        backdrop.remove();
        assert.deepEqual(body.children, [shell]);
        assert.equal(root.getAttribute("data-skin-background-active"), "native-marker");
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
