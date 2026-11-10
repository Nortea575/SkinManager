using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Runtime;

namespace SkinManager.Preview.Tests;

public static class RuntimeDomReadinessChecks
{
    public static async Task RunAsync(WebView2 browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("Runtime DOM readiness checks require an initialized WebView2.");
        var token = "preview-dom-readiness-" + Guid.NewGuid().ToString("N");
        var bindingName = "__skinManagerPreviewDomBinding";
        var frameId = "skin-manager-runtime-dom-readiness-frame";
        var applyPayload = "window.__runtimeDomPayloadCount=(window.__runtimeDomPayloadCount||0)+1;";
        var removePayload = "window.__runtimeDomRemoveCount=(window.__runtimeDomRemoveCount||0)+1;";
        // This is an isolated DOM-semantics check: only the guarded `location` input is shadowed.
        // The iframe keeps its real same-origin document, window, and MutationObserver; Runtime.Tests covers origin rejection.
        var buildGuardedScript = typeof(RuntimeController).Assembly
            .GetType("SkinManager.Runtime.CdpRuntimeSession", throwOnError: true)!
            .GetMethod("BuildGuardedScript", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The guarded runtime script builder was not found.");
        var applyScript = InvokeGuardBuilder(buildGuardedScript, applyPayload, token, bindingName, isRemove: false);
        var removeScript = InvokeGuardBuilder(buildGuardedScript, removePayload, token, bindingName, isRemove: true);

        await core.ExecuteScriptAsync($$"""
            (() => {
              const contexts = window.__runtimeDomReadinessContexts ??= Object.create(null);
              contexts[{{JsonSerializer.Serialize(frameId)}}]?.frame?.remove();
              delete contexts[{{JsonSerializer.Serialize(frameId)}}];
              const frame = document.createElement('iframe');
              frame.id = {{JsonSerializer.Serialize(frameId)}};
              let win;
              let doc;
              let mutationObserver;
              try {
                document.documentElement.appendChild(frame);
                win = frame.contentWindow;
                doc = win?.document;
                if (!win || !doc) throw new Error('about:blank frame was blocked');
                doc.open();
                doc.write('<!doctype html><html><body><div id="app-shell"><main id="main-surface"></main></div></body></html>');
                doc.close();
                if (!doc.getElementById('app-shell') || !doc.querySelector('main')) throw new Error('about:blank document setup was blocked');
                mutationObserver = win.MutationObserver;
                if (typeof mutationObserver !== 'function') throw new Error('about:blank frame has no MutationObserver');
              } catch {
                frame.remove();
                doc = document.implementation.createHTMLDocument('Runtime DOM readiness');
                doc.body.innerHTML = '<div id="app-shell"><main id="main-surface"></main></div>';
                win = {};
                mutationObserver = window.MutationObserver;
              }
              win.__runtimeDomPayloadCount = 0;
              win.__runtimeDomBindingCount = 0;
              win.__runtimeDomRemoveCount = 0;
              win.__runtimeDomReadinessTest = { turns: 0 };
              win[{{JsonSerializer.Serialize(bindingName)}}] = value => {
                if (value === {{JsonSerializer.Serialize(token)}}) win.__runtimeDomBindingCount++;
              };
              contexts[{{JsonSerializer.Serialize(frameId)}}] = { frame, window: win, document: doc, MutationObserver: mutationObserver };
              return true;
            })()
            """).ConfigureAwait(true);

        try
        {
            var pendingResult = await RunGuardAsync(core, frameId, applyScript).ConfigureAwait(true);
            Require(pendingResult == "false", "the real document observer waits while both shell attributes are absent");
            var pending = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(pending.PayloadCount == 0 && pending.BindingCount == 0 && !pending.HasAppliedToken && pending.HasObserver,
                "the pending apply registers its observer without injecting the payload or acknowledging it");

            await RunGuardAsync(core, frameId, removeScript).ConfigureAwait(true);
            var removedPending = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(!removedPending.HasObserver && !removedPending.HasAppliedToken && removedPending.RemoveCount == 0,
                "removing a pending apply clears its observer and token without running the remove payload");

            await SetAttributesAndWaitForTurnAsync(core, frameId, token, bindingName,
                ["data-app-shell-frame", "data-app-shell-main-surface"])
                .ConfigureAwait(true);
            var afterPendingRemoval = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(afterPendingRemoval.PayloadCount == 0 && afterPendingRemoval.BindingCount == 0,
                "DOM attributes changed after pending removal do not trigger the disconnected observer");
            await RemoveAttributesAsync(core, frameId, ["data-app-shell-frame", "data-app-shell-main-surface"])
                .ConfigureAwait(true);

            await RunGuardAsync(core, frameId, applyScript).ConfigureAwait(true);
            var waitingAgain = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(waitingAgain.PayloadCount == 0 && waitingAgain.HasObserver,
                "the same document can register a fresh observer after a pending removal");

            await SetAttributesAndWaitForTurnAsync(core, frameId, token, bindingName, ["data-app-shell-frame"])
                .ConfigureAwait(true);
            var shellOnly = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(shellOnly.PayloadCount == 0 && shellOnly.BindingCount == 0 && shellOnly.HasObserver,
                "adding only the shell attribute leaves the payload pending on the real MutationObserver");

            await SetAttributesAndWaitForTurnAsync(core, frameId, token, bindingName, ["data-app-shell-main-surface"])
                .ConfigureAwait(true);
            await WaitUntilAsync(async () =>
            {
                var state = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
                return state.PayloadCount == 1 && state.BindingCount == 1 && state.HasAppliedToken && !state.HasObserver;
            }, "attribute-only DOM readiness apply").ConfigureAwait(true);
            var ready = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(ready.PayloadCount == 1 && ready.BindingCount == 1 && ready.HasAppliedToken && !ready.HasObserver,
                "setting the main-surface attribute applies once, acknowledges once, and disconnects the observer");

            await SetAttributesAndWaitForTurnAsync(core, frameId, token, bindingName,
                ["data-app-shell-frame", "data-app-shell-main-surface"])
                .ConfigureAwait(true);
            await RunGuardAsync(core, frameId, applyScript).ConfigureAwait(true);
            var duplicateGuard = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(duplicateGuard.PayloadCount == 1 && duplicateGuard.BindingCount == 1,
                "later attribute mutations and explicit guard reevaluation do not inject or acknowledge twice");

            var removeResult = await RunGuardAsync(core, frameId, removeScript).ConfigureAwait(true);
            var removedApplied = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            Require(removeResult == "true" && removedApplied.RemoveCount == 1 &&
                    !removedApplied.HasAppliedToken && !removedApplied.HasObserver,
                "removing an applied runtime runs its remove payload and clears both observer and token");

            Console.WriteLine("通过：Runtime DOM readiness applies only after real shell attributes change and never injects twice.");
        }
        finally
        {
            try
            {
                await core.ExecuteScriptAsync($"(() => {{ const contexts = window.__runtimeDomReadinessContexts; contexts?.[{JsonSerializer.Serialize(frameId)}]?.frame?.remove(); if (contexts) delete contexts[{JsonSerializer.Serialize(frameId)}]; return true; }})()")
                    .ConfigureAwait(true);
            }
            catch
            {
                // Keep a browser teardown/navigation error from masking the assertion that failed above.
            }
        }
    }

    private static string InvokeGuardBuilder(MethodInfo method, string script, string token, string bindingName, bool isRemove) =>
        method.Invoke(null, [script, token, bindingName, isRemove]) as string
        ?? throw new InvalidOperationException("The guarded runtime script builder returned no script.");

    private static async Task<string> RunGuardAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, string frameId, string guardScript)
    {
        var frameIdentifier = JsonSerializer.Serialize(frameId);
        return await core.ExecuteScriptAsync($$"""
            (() => {
              const context = window.__runtimeDomReadinessContexts?.[{{frameIdentifier}}];
              if (!context) throw new Error('The isolated runtime test DOM is missing.');
              const win = context.window;
              const doc = context.document;
              const evaluate = (location, window, document, MutationObserver) => {{guardScript}};
              return evaluate({ protocol: 'app:', hostname: 'codex' }, win, doc, context.MutationObserver);
            })()
            """).ConfigureAwait(true);
    }

    private static async Task<DomState> ReadStateAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string frameId,
        string token,
        string bindingName)
    {
        var frameIdentifier = JsonSerializer.Serialize(frameId);
        var tokenKey = JsonSerializer.Serialize("__skinManagerApplied_" + token);
        var observerKey = JsonSerializer.Serialize("__skinManagerObserver_" + token);
        var binding = JsonSerializer.Serialize(bindingName);
        var result = await core.ExecuteScriptAsync($$"""
            (() => {
              const context = window.__runtimeDomReadinessContexts?.[{{frameIdentifier}}];
              if (!context) throw new Error('The isolated runtime test DOM is missing.');
              const win = context.window;
              return JSON.stringify({
                payloadCount: win.__runtimeDomPayloadCount,
                bindingCount: win.__runtimeDomBindingCount,
                removeCount: win.__runtimeDomRemoveCount,
                hasAppliedToken: win[{{tokenKey}}] === true,
                hasObserver: Boolean(win[{{observerKey}}]),
                mutationTurns: win.__runtimeDomReadinessTest.turns,
                bindingNameExists: typeof win[{{binding}}] === 'function'
              });
            })()
            """).ConfigureAwait(true);
        var json = JsonSerializer.Deserialize<string>(result)
            ?? throw new InvalidDataException("The isolated runtime frame returned no state.");
        return JsonSerializer.Deserialize<DomState>(json)
            ?? throw new InvalidDataException("The isolated runtime frame returned malformed state.");
    }

    private static async Task SetAttributesAndWaitForTurnAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string frameId,
        string token,
        string bindingName,
        IReadOnlyList<string> attributes)
    {
        var previousTurn = (await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true)).MutationTurns;
        await MutateAttributesAsync(core, frameId, attributes, set: true).ConfigureAwait(true);
        await WaitUntilAsync(async () =>
        {
            var state = await ReadStateAsync(core, frameId, token, bindingName).ConfigureAwait(true);
            return state.MutationTurns > previousTurn;
        }, "DOM mutation observer turn").ConfigureAwait(true);
    }

    private static async Task RemoveAttributesAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string frameId,
        IReadOnlyList<string> attributes) =>
        await MutateAttributesAsync(core, frameId, attributes, set: false).ConfigureAwait(true);

    private static async Task MutateAttributesAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core,
        string frameId,
        IReadOnlyList<string> attributes,
        bool set)
    {
        var frameIdentifier = JsonSerializer.Serialize(frameId);
        var attributeList = JsonSerializer.Serialize(attributes);
        var action = set
            ? "for (const name of names) { (name === 'data-app-shell-frame' ? shell : surface).setAttribute(name, 'true'); }"
            : "for (const name of names) { (name === 'data-app-shell-frame' ? shell : surface).removeAttribute(name); }";
        await core.ExecuteScriptAsync($$"""
            (() => {
              const context = window.__runtimeDomReadinessContexts?.[{{frameIdentifier}}];
              if (!context) throw new Error('The isolated runtime test DOM is missing.');
              const doc = context.document;
              const shell = doc.getElementById('app-shell');
              const surface = doc.getElementById('main-surface');
              const names = {{attributeList}};
              {{action}}
              window.setTimeout(() => context.window.__runtimeDomReadinessTest.turns++, 0);
              return true;
            })()
            """).ConfigureAwait(true);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string label)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (await condition().ConfigureAwait(true))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(true);
        }

        throw new TimeoutException($"Timed out while waiting for {label} in the isolated WebView2 document.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Runtime DOM readiness check failed: {message}");
        }
    }

    private sealed record DomState(
        [property: JsonPropertyName("payloadCount")] int PayloadCount,
        [property: JsonPropertyName("bindingCount")] int BindingCount,
        [property: JsonPropertyName("removeCount")] int RemoveCount,
        [property: JsonPropertyName("hasAppliedToken")] bool HasAppliedToken,
        [property: JsonPropertyName("hasObserver")] bool HasObserver,
        [property: JsonPropertyName("mutationTurns")] int MutationTurns,
        [property: JsonPropertyName("bindingNameExists")] bool BindingNameExists);
}
