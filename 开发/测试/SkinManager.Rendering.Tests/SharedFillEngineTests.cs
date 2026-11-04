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
        Require(runtimeScript.Contains("getComputedStyle: (element) => getComputedStyle(element)", StringComparison.Ordinal) &&
                previewScript.Contains("getComputedStyle: (element) => getComputedStyle(element)", StringComparison.Ordinal),
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
          remove() { this.removed = true; }
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
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
