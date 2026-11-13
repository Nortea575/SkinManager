using System.Diagnostics;
using System.Text.Json;
using SkinManager.Runtime;

internal static class LegacyLocaleRestoreScriptTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SkinManager-LegacyLocaleScript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptsPath = Path.Combine(root, "legacy-restore-scripts.json");
            var runnerPath = Path.Combine(root, "legacy-restore-runner.mjs");
            var scripts = new Dictionary<string, string>
            {
                ["disable"] = CodexLocaleCompatibilityScript.BuildDisableLegacyHookScript("g1"),
                ["staleRestore"] = CodexLocaleCompatibilityScript.BuildRestoreLocaleOverrideScript("g2", "fr-FR"),
                ["restore"] = CodexLocaleCompatibilityScript.BuildRestoreLocaleOverrideScript("g1", "fr-FR")
            };
            await File.WriteAllTextAsync(scriptsPath, JsonSerializer.Serialize(scripts));
            await File.WriteAllTextAsync(runnerPath, NodeRunner);

            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--experimental-vm-modules");
            startInfo.ArgumentList.Add(runnerPath);
            startInfo.ArgumentList.Add(scriptsPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the offline Node.js VM runner.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new TimeoutException("The offline legacy locale VM runner exceeded its 20 second limit.");
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Legacy locale restore script tests failed.\n" + error + output);

            using var result = JsonDocument.Parse(output);
            var assertions = result.RootElement.GetProperty("assertions").GetInt32();
            if (result.RootElement.GetProperty("passed").GetBoolean() != true || assertions < 6)
                throw new InvalidOperationException("The legacy locale VM runner did not report the expected assertions.\n" + output);

            Console.WriteLine($"LegacyLocaleRestoreScriptTests: {assertions} assertions passed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string NodeRunner = """
        import { readFile } from 'node:fs/promises';
        import vm from 'node:vm';

        const scripts = JSON.parse(await readFile(process.argv[2], 'utf8'));
        let assertions = 0;
        const check = (message, condition) => {
          assertions++;
          if (!condition) throw new Error(`Assertion failed: ${message}`);
        };

        function makeWorld(initialLocale, withLegacyHook = false) {
          let locale = initialLocale;
          const writes = [];
          const settings = {
            read() { return Promise.resolve({ effective: locale }); },
            write(key, value) {
              writes.push({ key, value });
              locale = value;
              return Promise.resolve({ effective: locale });
            }
          };
          const shared = { appServices: { settings } };
          const runtime = id => {
            if (id === 'GaA7') return shared;
            throw new Error('unexpected fake module request: ' + id);
          };
          runtime.c = { settingsModule: { exports: shared } };
          let module;
          const sandbox = {
            location: { protocol: 'app:', hostname: 'codex' },
            electronBridge: {},
            document: { scripts: [{ type: 'module', src: 'app://-/assets/fake-entry.js' }] },
            AbortController,
            Symbol,
            setTimeout,
            clearTimeout
          };
          sandbox.window = sandbox;
          sandbox.top = sandbox;
          let legacyLayer = null;
          if (withLegacyHook) {
            legacyLayer = { get(key) { return key === 'enable_i18n' ? false : `original:${key}`; } };
            const descriptor = Object.getOwnPropertyDescriptor(legacyLayer, 'get');
            const original = legacyLayer.get;
            const wrapper = function (key, ...args) {
              if (key === 'enable_i18n') return true;
              return Reflect.apply(original, this, [key, ...args]);
            };
            legacyLayer.get = wrapper;
            sandbox.__skinManagerLocaleCompatibility = {
              generation: 'old-generation',
              enabled: true,
              patches: [{ target: legacyLayer, key: 'get', wrapper, hadOwn: true, descriptor }],
              globalHook: null,
              timerIds: []
            };
          }

          const context = vm.createContext(sandbox);
          const importModuleDynamically = async source => {
            if (source !== 'app://-/assets/fake-entry.js') throw new Error('unexpected import: ' + source);
            if (!module) {
              module = new vm.SyntheticModule(['__webpack_require__'], function () {
                this.setExport('__webpack_require__', runtime);
              }, { context });
              await module.link(() => { throw new Error('fake entry has no dependencies'); });
              await module.evaluate();
            }
            return module;
          };
          return {
            context,
            writes,
            get locale() { return locale; },
            get legacyLayer() { return legacyLayer; },
            get legacyState() { return sandbox.__skinManagerLocaleCompatibility; },
            run(script) { return vm.runInContext(script, context, { importModuleDynamically }); }
          };
        }

        const restoredWithoutOldHook = makeWorld('zh-CN');
        const disabled = await restoredWithoutOldHook.run(scripts.disable);
        check('disable initializes a one-shot disabled state when the old hook is absent',
          disabled.state === 'disabled' && restoredWithoutOldHook.legacyState?.enabled === false && restoredWithoutOldHook.legacyState?.generation === 'g1');
        const restored = await restoredWithoutOldHook.run(scripts.restore);
        check('a legacy snapshot restores after Codex restarted without the old hook',
          restored.state === 'restored' && restoredWithoutOldHook.locale === 'fr-FR' && restoredWithoutOldHook.writes.length === 1);

        const restoredLegacyHook = makeWorld('zh-CN', true);
        await restoredLegacyHook.run(scripts.disable);
        check('an existing legacy wrapper is removed and the disabled generation is installed',
          restoredLegacyHook.legacyState?.enabled === false && restoredLegacyHook.legacyState?.generation === 'g1' &&
          restoredLegacyHook.legacyLayer.get('enable_i18n') === false);
        const restoredAfterDisable = await restoredLegacyHook.run(scripts.restore);
        check('the original locale is restored after disabling an existing hook',
          restoredAfterDisable.state === 'restored' && restoredLegacyHook.locale === 'fr-FR' && restoredLegacyHook.writes.length === 1);

        const userChangedLocale = makeWorld('de-DE');
        await userChangedLocale.run(scripts.disable);
        const preserved = await userChangedLocale.run(scripts.restore);
        check('a locale changed by the user is preserved', preserved.state === 'preserved' && userChangedLocale.locale === 'de-DE' && userChangedLocale.writes.length === 0);

        const staleRestore = makeWorld('zh-CN');
        await staleRestore.run(scripts.disable);
        const cancelled = await staleRestore.run(scripts.staleRestore);
        check('a stale generation cannot write a locale value', cancelled.state === 'cancelled' && staleRestore.locale === 'zh-CN' && staleRestore.writes.length === 0);

        console.log(JSON.stringify({ passed: true, assertions }));
        """;
}
