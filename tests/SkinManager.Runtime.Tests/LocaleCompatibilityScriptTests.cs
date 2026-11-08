using System.Diagnostics;
using System.Text.Json;
using SkinManager.Runtime;

internal static class LocaleCompatibilityScriptTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SkinManager-LocaleScript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scripts = new Dictionary<string, string>
            {
                ["stateOn"] = CodexLocaleCompatibilityScript.BuildStateScript("g1", enabled: true),
                ["stateOnAgain"] = CodexLocaleCompatibilityScript.BuildStateScript("g1", enabled: true),
                ["stateOff"] = CodexLocaleCompatibilityScript.BuildStateScript("g2", enabled: false),
                ["lateStateOn"] = CodexLocaleCompatibilityScript.BuildStateScript("late-on", enabled: true),
                ["lateStateOff"] = CodexLocaleCompatibilityScript.BuildStateScript("late-off", enabled: false),
                ["read"] = CodexLocaleCompatibilityScript.BuildReadLocaleOverrideScript("g1"),
                ["write"] = CodexLocaleCompatibilityScript.BuildWriteSimplifiedChineseScript("g1"),
                ["restore"] = CodexLocaleCompatibilityScript.BuildRestoreLocaleOverrideScript("g2", "fr-FR"),
                ["lateRead"] = CodexLocaleCompatibilityScript.BuildReadLocaleOverrideScript("late-on")
            };
            var scriptsPath = Path.Combine(root, "generated-scripts.json");
            var runnerPath = Path.Combine(root, "locale-script-runner.mjs");
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
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new TimeoutException("The offline Node.js VM runner exceeded its 20 second limit.");
            }

            var output = await standardOutput;
            var error = await standardError;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Locale compatibility JavaScript behavior tests failed.\n" + error + output);

            using var result = JsonDocument.Parse(output);
            var nodeVersion = result.RootElement.GetProperty("nodeVersion").GetString();
            var assertions = result.RootElement.GetProperty("assertions").GetInt32();
            if (result.RootElement.GetProperty("passed").GetBoolean() != true || assertions < 20)
                throw new InvalidOperationException("The Node.js VM runner did not report the expected behavior assertions.\n" + output);

            Console.WriteLine($"LocaleCompatibilityScriptTests: Node {nodeVersion}, {assertions} assertions passed.");
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
        let assertionCount = 0;
        const check = (message, condition) => {
          assertionCount++;
          if (!condition) throw new Error(`Assertion failed: ${message}`);
        };
        const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
        const waitFor = async (predicate, message, timeoutMs = 1500) => {
          const started = Date.now();
          while (!predicate()) {
            if (Date.now() - started >= timeoutMs) throw new Error(`Timed out waiting for ${message}`);
            await delay(10);
          }
        };

        function makeClient() {
          const layer = {
            get(key) { return key === 'enable_i18n' ? false : `original:${key}`; }
          };
          return {
            layer,
            client: { getLayer(name) { return name === '72216192' ? layer : null; } }
          };
        }

        function makeEnvironment({ initialLocale = 'fr-FR', loader = 'fallback', app = true,
          top = true, statsig = undefined, readBehavior = 'normal' } = {}) {
          const log = { reads: [], writes: [], imports: 0, gaA7Requests: 0, q77yRequests: 0, disposals: 0 };
          let locale = initialLocale;
          let pendingReadResolve;
          const settings = {
            read(key) {
              log.reads.push(key);
              if (readBehavior === 'deferred') {
                return {
                  then(resolve) { pendingReadResolve = resolve; },
                  [Symbol.dispose]() { log.disposals++; }
                };
              }
              if (readBehavior === 'never-settles') {
                return {
                  then() {},
                  [Symbol.dispose]() { log.disposals++; }
                };
              }
              return Promise.resolve({ effective: locale });
            },
            write(key, value) {
              log.writes.push({ key, value });
              locale = value;
              return Promise.resolve({ effective: locale });
            }
          };
          const sharedExports = { appServices: { settings } };
          const runtime = id => {
            if (id === 'GaA7') {
              log.gaA7Requests++;
              if (loader === 'fallback') return sharedExports;
              throw new Error('fake GaA7 module is not available');
            }
            if (id === 'q77y') {
              log.q77yRequests++;
              throw new Error('use the generated thenable fallback');
            }
            throw new Error(`unexpected fake module request: ${id}`);
          };
          if (loader === 'cache')
            runtime.c = { settingsModule: { exports: sharedExports } };

          const sandbox = {
            location: { protocol: app ? 'app:' : 'https:', hostname: 'codex' },
            electronBridge: app ? {} : undefined,
            document: { scripts: [{ type: 'module', src: 'app://-/assets/fake-entry.js' }] },
            AbortController,
            Symbol,
            setTimeout,
            clearTimeout
          };
          sandbox.window = sandbox;
          sandbox.top = top ? sandbox : {};
          if (statsig !== undefined) sandbox.__STATSIG__ = statsig;
          const context = vm.createContext(sandbox);
          let module;
          const importModuleDynamically = async source => {
            log.imports++;
            if (source !== 'app://-/assets/fake-entry.js') throw new Error(`unexpected fake import: ${source}`);
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
            sandbox,
            log,
            importModuleDynamically,
            get locale() { return locale; },
            setLocale(value) { locale = value; },
            resolvePendingRead(value) { pendingReadResolve?.(value); },
            get pendingReadStarted() { return typeof pendingReadResolve === 'function'; }
          };
        }

        const run = async (environment, scriptName) => await vm.runInContext(scripts[scriptName], environment.sandbox, {
          importModuleDynamically: environment.importModuleDynamically
        });

        async function testLocaleRoundTripAndFallback() {
          const environment = makeEnvironment();
          const hook = await run(environment, 'stateOn');
          check('app context installs the Statsig hook', hook.state === 'hooked');
          const initial = await run(environment, 'read');
          check('locale read uses the fake native settings service', initial.state === 'ready' && initial.value === 'fr-FR');
          const applied = await run(environment, 'write');
          check('locale write is read back before reporting applied', applied.state === 'applied' && applied.value === 'zh-CN' && environment.locale === 'zh-CN');
          await run(environment, 'stateOff');
          const restored = await run(environment, 'restore');
          check('restore writes and verifies the original locale', restored.state === 'restored' && restored.value === 'fr-FR' && environment.locale === 'fr-FR');
          check('fallback loads GaA7 and performs service reads around writes', environment.log.gaA7Requests > 0 &&
            environment.log.reads.length >= 5 && environment.log.writes.length === 2 &&
            environment.log.writes[0].value === 'zh-CN' && environment.log.writes[1].value === 'fr-FR');
          check('the generated host scripts import only the declared fake app entry', environment.log.imports >= 5);
        }

        async function testCachedExportsShape() {
          const environment = makeEnvironment({ initialLocale: null, loader: 'cache' });
          await run(environment, 'stateOn');
          const result = await run(environment, 'read');
          await run(environment, 'stateOff');
          check('cache exports with appServices.settings are detected without GaA7 fallback',
            result.state === 'ready' && result.value === null && environment.log.gaA7Requests === 0 && environment.log.reads.length === 1);
        }

        async function testManualLocaleIsPreserved() {
          const environment = makeEnvironment();
          await run(environment, 'stateOn');
          const applied = await run(environment, 'write');
          check('the test begins from a confirmed manager locale write', applied.state === 'applied' && environment.locale === 'zh-CN');
          environment.setLocale('ja-JP');
          await run(environment, 'stateOff');
          const writeCount = environment.log.writes.length;
          const restored = await run(environment, 'restore');
          check('restore preserves a language changed by the user', restored.state === 'preserved' && restored.value === 'ja-JP' && environment.locale === 'ja-JP');
          check('preserving a user change does not send a compensating write', environment.log.writes.length === writeCount);
        }

        async function testStatsigInstancesAndRestoration() {
          const first = makeClient();
          const fromInstance = makeClient();
          const statsig = { firstInstance: first.client, instance() { return fromInstance.client; } };
          const environment = makeEnvironment({ statsig });
          const originalRefs = [first.client.getLayer, first.layer.get, fromInstance.client.getLayer, fromInstance.layer.get];
          await run(environment, 'stateOn');
          check('Statsig firstInstance and instance() clients are patched',
            first.client.getLayer('72216192').get('enable_i18n') === true &&
            fromInstance.client.getLayer('72216192').get('enable_i18n') === true);
          const wrappers = [first.client.getLayer, first.layer.get, fromInstance.client.getLayer, fromInstance.layer.get];
          await run(environment, 'stateOnAgain');
          check('re-enabling does not stack Statsig wrappers',
            wrappers.every((wrapper, index) => wrapper === [first.client.getLayer, first.layer.get, fromInstance.client.getLayer, fromInstance.layer.get][index]));
          await run(environment, 'stateOff');
          check('disable restores exact original getLayer/get references',
            [first.client.getLayer, first.layer.get, fromInstance.client.getLayer, fromInstance.layer.get]
              .every((reference, index) => reference === originalRefs[index]));
          check('disable restores the original Statsig global value', environment.sandbox.__STATSIG__ === statsig);
        }

        async function testLateStatsigClient() {
          const statsig = {};
          const environment = makeEnvironment({ statsig });
          const late = makeClient();
          const originalGetLayer = late.client.getLayer;
          const originalGet = late.layer.get;
          const hookPending = run(environment, 'lateStateOn');
          await delay(30);
          statsig.firstInstance = late.client;
          const hook = await hookPending;
          check('the pending hook observes a client attached to an existing Statsig root',
            hook.state === 'hooked' && hook.hookInstalled &&
            late.client.getLayer('72216192').get('enable_i18n') === true);
          await run(environment, 'lateStateOff');
          check('the late-attached Statsig client is patched by retry and then restored',
            late.client.getLayer === originalGetLayer && late.layer.get === originalGet);
        }

        async function testStalePendingReadDoesNotWrite() {
          const environment = makeEnvironment({ readBehavior: 'deferred' });
          await run(environment, 'stateOn');
          const pendingWrite = run(environment, 'write');
          await waitFor(() => environment.pendingReadStarted, 'pending fake locale read');
          await run(environment, 'stateOff');
          environment.resolvePendingRead({ effective: 'de-DE' });
          const result = await pendingWrite;
          check('closing the locale operation cancels its old pending read', result.state === 'cancelled');
          check('the stale operation never writes after the next generation disables it', environment.log.writes.length === 0);
        }

        async function testNeverSettlingTaskTimesOutAndDisposes() {
          const environment = makeEnvironment({ readBehavior: 'never-settles' });
          await run(environment, 'stateOn');
          const started = Date.now();
          const result = await run(environment, 'read');
          const elapsed = Date.now() - started;
          await run(environment, 'stateOff');
          check('a never-settling settings task returns pending within the finite timeout',
            result.state === 'pending' && elapsed >= 1200 && elapsed < 3500);
          check('timeout disposes the never-settling thenable', environment.log.disposals === 1);
        }

        async function testNonAppAndSubframeNeverCallNative() {
          for (const options of [{ app: false }, { top: false }]) {
            const environment = makeEnvironment(options);
            const hook = await run(environment, 'stateOn');
            const read = await run(environment, 'read');
            const write = await run(environment, 'write');
            const restore = await run(environment, 'restore');
            check(`unsupported ${options.app === false ? 'non-app' : 'subframe'} hook exits early`, hook.state === 'unsupported');
            check(`unsupported ${options.app === false ? 'non-app' : 'subframe'} host scripts exit early`,
              read.state === 'unsupported' && write.state === 'unsupported' && restore.state === 'unsupported');
            check(`unsupported ${options.app === false ? 'non-app' : 'subframe'} never imports or calls native settings`,
              environment.log.imports === 0 && environment.log.reads.length === 0 && environment.log.writes.length === 0);
          }
        }

        try {
          await testLocaleRoundTripAndFallback();
          await testCachedExportsShape();
          await testManualLocaleIsPreserved();
          await testStatsigInstancesAndRestoration();
          await testLateStatsigClient();
          await testStalePendingReadDoesNotWrite();
          await testNeverSettlingTaskTimesOutAndDisposes();
          await testNonAppAndSubframeNeverCallNative();
          console.log(JSON.stringify({ passed: true, nodeVersion: process.version, assertions: assertionCount }));
        } catch (error) {
          console.error(error?.stack || error);
          process.exitCode = 1;
        }
        """;
}
