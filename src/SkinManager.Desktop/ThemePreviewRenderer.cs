using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SkinManager.Core;
using SkinManager.Rendering;

namespace SkinManager.Desktop;

public sealed class ThemePreviewRenderer : IDisposable
{
    private const string PreviewDialogBootstrapScript = """
        (() => {
          const root = document.documentElement;
          const installKey = "__SKIN_MANAGER_PREVIEW_DIALOGS__";
          if (window[installKey]) return;

          const actionToDialog = new Map([
            ["dialog:model", "model"],
            ["dialog:attach", "attach"],
            ["dialog:workspace", "workspace"],
            ["dialog:thread-menu", "thread-menu"],
          ]);
          const dialogNames = new Set(actionToDialog.values());
          const setDialog = (name) => root.setAttribute("data-fixture-dialog", name);
          const closeDialog = () => {
            if (dialogNames.has(root.getAttribute("data-fixture-dialog"))) setDialog("none");
          };

          document.addEventListener("click", (event) => {
            const target = event.target instanceof Element ? event.target : null;
            if (!target) return;

            const action = target.closest("[data-fixture-action]")?.getAttribute("data-fixture-action");
            if (actionToDialog.has(action)) {
              event.preventDefault();
              setDialog(actionToDialog.get(action));
              return;
            }
            if (action === "dialog:none") {
              event.preventDefault();
              setDialog("none");
              return;
            }

            const overlay = target.closest("[data-fixture-dialog-overlay]");
            if (overlay && overlay === target) closeDialog();
          }, true);

          document.addEventListener("keydown", (event) => {
            if (event.key === "Escape" && dialogNames.has(root.getAttribute("data-fixture-dialog"))) {
              event.preventDefault();
              setDialog("none");
            }
          }, true);

          window[installKey] = true;
        })()
        """;
    private const string FixturePageRoutingBootstrapTemplate = """
        (() => {
          if (location.protocol !== "https:" ||
              location.hostname !== "codex-preview.invalid" ||
              !/^\/[0-9a-f]{32}\/base\//i.test(location.pathname)) return;

          const root = document.documentElement;
          const activePage = __FIXTURE_PAGE_JSON__;
          const sidebarOpen = root.getAttribute("data-fixture-sidebar") === "open";
          document.querySelector(__CODEX_APP_FRAME_SELECTOR__)?.setAttribute("data-app-shell-sidebar-open", sidebarOpen ? "true" : "false");
          for (const panel of document.querySelectorAll("[data-fixture-page-panel]")) {
            if (panel.getAttribute("data-fixture-page-panel") !== activePage) panel.remove();
          }
          const activeComposerPlacement = activePage === "home" ? "home" : "thread";
          for (const composer of document.querySelectorAll(
            '[data-codex-composer-root][data-composer-placement="home"], [data-codex-composer-root][data-composer-placement="thread"]',
          )) {
            if (composer.getAttribute("data-composer-placement") !== activeComposerPlacement) composer.remove();
          }
        })()
        """;

    private readonly WebView2 _webView;
    private readonly BasePackage _basePackage;
    private readonly string _userDataFolder;
    private readonly SemaphoreSlim _displayGate = new(1, 1);
    private static readonly HashSet<string> SelectableParts = SkinDefinitionCatalog.Components
        .Where(item => item.EditorVisible)
        .Select(item => item.Id)
        .ToHashSet(StringComparer.Ordinal);
    private Task<CoreWebView2>? _initializationTask;
    private PreviewDocument? _currentDocument;
    private TaskCompletionSource<bool>? _navigationCompletion;
    private ulong? _activeNavigationId;
    private bool _disposed;
    private bool _isSuspended;
    private bool _currentDocumentReady;
    private bool _componentSelectionMode;
    private long _displayRequestSequence;
    private ResolvedSkin? _currentResolvedSkin;

    public ThemePreviewRenderer(WebView2 webView, string userDataFolder, BasePackage basePackage)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _userDataFolder = Path.GetFullPath(userDataFolder ?? throw new ArgumentNullException(nameof(userDataFolder)));
        _basePackage = basePackage ?? throw new ArgumentNullException(nameof(basePackage));
    }

    public int ServedStylesheetRequests { get; private set; }
    public int ServedPackageResourceRequests { get; private set; }
    public int ServedBaseResourceRequests { get; private set; }
    public int BlockedExternalRequests { get; private set; }
    public int BlockedExternalNavigationRequests { get; private set; }
    public ResolvedSkin? CurrentResolvedSkin => _currentResolvedSkin;
    public event EventHandler? CurrentResolvedSkinChanged;
    public event EventHandler<PreviewComponentSelectedEventArgs>? ComponentSelected;
    public List<string> ServedResourcePaths { get; } = [];
    public List<string> ServedBaseResourcePaths { get; } = [];
    public List<string> ServedSkinResourcePaths { get; } = [];

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        _ = await EnsureInitializedAsync();
    }

    public async Task SetSuspendedAsync(bool suspended)
    {
        ThrowIfDisposed();
        var initialization = _initializationTask;
        if (initialization is null || !initialization.IsCompletedSuccessfully)
        {
            return;
        }

        await _displayGate.WaitAsync();
        try
        {
            if (_disposed || _webView.CoreWebView2 is not { } core)
            {
                return;
            }

            if (suspended)
            {
                if (!_isSuspended)
                {
                    _isSuspended = await core.TrySuspendAsync();
                }
            }
            else if (_isSuspended)
            {
                core.Resume();
                _isSuspended = false;
            }
        }
        finally
        {
            _displayGate.Release();
        }
    }

    public Task<bool> DisplayBaseAsync(string appearance) =>
        DisplayAsync(null, new PreviewOptions(Appearance: appearance));

    public Task<bool> DisplayBaseAsync(PreviewOptions options) => DisplayAsync(null, options);

    public Task<bool> DisplayPackageAsync(SkinPackage package, string? appearance = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var resolvedPreference = appearance ?? package.Appearance.ToString().ToLowerInvariant();
        return DisplayAsync(package, new PreviewOptions(Appearance: resolvedPreference));
    }

    public Task<bool> DisplayPackageAsync(SkinPackage package, PreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        return DisplayAsync(package, options);
    }

    public async Task<bool> UpdatePackageStylesAsync(SkinPackage package, PreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();

        var scene = PreviewDocumentBuilder.NormalizeScene(options.Scene);
        var appearance = PreviewDocumentBuilder.NormalizeAppearance(options.Appearance);
        var current = _currentDocument;
        if (!_currentDocumentReady || current?.Package is not { } currentPackage ||
            !string.Equals(currentPackage.Id, package.Id, StringComparison.OrdinalIgnoreCase) ||
            current.Options.ShowOriginalBase ||
            options.ShowOriginalBase ||
            !string.Equals(current.Options.Scene, scene, StringComparison.Ordinal) ||
            !string.Equals(current.AppearancePreference, appearance, StringComparison.Ordinal) ||
            current.Options.SidebarVisible != options.SidebarVisible)
        {
            return await DisplayAsync(package, options);
        }

        var requestSequence = Interlocked.Increment(ref _displayRequestSequence);
        _navigationCompletion?.TrySetResult(false);
        await _displayGate.WaitAsync();
        var fallback = false;
        try
        {
            if (!IsLatestDisplayRequest(requestSequence))
            {
                return false;
            }

            if (!_currentDocumentReady || _currentDocument is not { Package: { } latestPackage } latest ||
                !string.Equals(latestPackage.Id, package.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(latest.Generation, current.Generation, StringComparison.Ordinal))
            {
                fallback = true;
            }
            else
            {
                var core = await EnsureInitializedAsync();
                if (!IsLatestDisplayRequest(requestSequence))
                {
                    return false;
                }

                ResumeIfSuspended(core);
                var updated = latest with
                {
                    Package = package,
                    Options = options with { Scene = scene, Appearance = appearance },
                    AppearancePreference = appearance
                };
                _currentDocument = updated;
                var currentAppearance = await GetActualAppearanceAsync(core, updated);
                if (!IsLatestDisplayRequest(requestSequence))
                {
                    return false;
                }

                await ApplyAppearanceAsync(core, updated, currentAppearance,
                    () => IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, updated));
                return IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, updated);
            }
        }
        finally
        {
            _displayGate.Release();
        }

        return fallback && await DisplayAsync(package, options);
    }

    public async Task SetComponentSelectionModeAsync(bool enabled)
    {
        ThrowIfDisposed();
        _componentSelectionMode = enabled;
        if (_currentDocument is not { } document || _initializationTask is not { IsCompletedSuccessfully: true } initialization)
        {
            return;
        }

        var core = await initialization;
        if (ReferenceEquals(_currentDocument, document) && !_disposed)
        {
            await core.ExecuteScriptAsync(BuildComponentSelectionScript(enabled, document.Generation));
        }
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var core = await EnsureInitializedAsync();
        return await core.ExecuteScriptAsync(script);
    }

    public async Task CapturePreviewAsync(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var core = await EnsureInitializedAsync();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, destination);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isSuspended = false;
        _currentDocumentReady = false;
        _componentSelectionMode = false;
        Interlocked.Increment(ref _displayRequestSequence);
        _navigationCompletion?.TrySetCanceled();
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebResourceRequested -= Core_WebResourceRequested;
            core.NavigationStarting -= Core_NavigationStarting;
            core.NavigationCompleted -= Core_NavigationCompleted;
            core.WebMessageReceived -= Core_WebMessageReceived;
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.DownloadStarting -= Core_DownloadStarting;
        }

        _webView.Dispose();
        _initializationTask = null;
        _currentDocument = null;
        _navigationCompletion = null;
        _activeNavigationId = null;
        SetCurrentResolvedSkin(null);
    }

    private async Task<bool> DisplayAsync(SkinPackage? package, PreviewOptions options)
    {
        ThrowIfDisposed();
        var requestedDocument = PreviewDocumentBuilder.Build(_basePackage, package, options);
        var requestSequence = Interlocked.Increment(ref _displayRequestSequence);
        _navigationCompletion?.TrySetResult(false);
        await _displayGate.WaitAsync();
        try
        {
            if (!IsLatestDisplayRequest(requestSequence))
            {
                return false;
            }

            var core = await EnsureInitializedAsync();
            if (!IsLatestDisplayRequest(requestSequence))
            {
                return false;
            }

            ResumeIfSuspended(core);

            if (_currentDocument is { } currentDocument && _currentDocumentReady &&
                CanUpdateTransparencyInPlace(currentDocument, requestedDocument))
            {
                var updatedDocument = currentDocument with { Options = requestedDocument.Options };
                _currentDocument = updatedDocument;
                var currentAppearance = await GetActualAppearanceAsync(core, updatedDocument);
                if (!IsLatestDisplayRequest(requestSequence))
                {
                    return false;
                }

                await ApplyAppearanceAsync(core, updatedDocument, currentAppearance,
                    () => IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, updatedDocument));
                return IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, updatedDocument);
            }

            _currentDocument = requestedDocument;
            _currentDocumentReady = false;
            SetCurrentResolvedSkin(null);

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigationCompletion = completion;
            _activeNavigationId = null;
            core.Navigate(requestedDocument.DocumentUri);

            var success = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!success || !ReferenceEquals(_currentDocument, requestedDocument))
            {
                return false;
            }

            await ApplyPreviewAsync(core, requestedDocument,
                () => IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, requestedDocument) && !_disposed);
            var completed = IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, requestedDocument);
            if (completed)
            {
                _currentDocumentReady = true;
            }

            return completed;
        }
        finally
        {
            _displayGate.Release();
        }
    }

    private bool IsLatestDisplayRequest(long requestSequence) =>
        !_disposed && Volatile.Read(ref _displayRequestSequence) == requestSequence;

    private void ResumeIfSuspended(CoreWebView2 core)
    {
        if (_isSuspended)
        {
            core.Resume();
            _isSuspended = false;
        }
    }

    private static bool CanUpdateTransparencyInPlace(PreviewDocument current, PreviewDocument requested) =>
        current.Package is not null && ReferenceEquals(current.Package, requested.Package) &&
        !current.Options.ShowOriginalBase && !requested.Options.ShowOriginalBase &&
        string.Equals(current.Options.Scene, requested.Options.Scene, StringComparison.Ordinal) &&
        string.Equals(current.Options.Appearance, requested.Options.Appearance, StringComparison.Ordinal) &&
        current.Options.SidebarVisible == requested.Options.SidebarVisible &&
        current.Options.UserTransparency != requested.Options.UserTransparency;

    private async Task ApplyPreviewAsync(CoreWebView2 core, PreviewDocument document, Func<bool> isCurrent)
    {
        if (!isCurrent()) return;
        await core.ExecuteScriptAsync(BuildFixturePageRoutingBootstrap(document.FixturePage));
        if (!isCurrent()) return;
        await core.ExecuteScriptAsync(PreviewDialogBootstrapScript);
        if (!isCurrent()) return;
        var appearance = await GetActualAppearanceAsync(core, document);
        if (!isCurrent()) return;
        await ApplyAppearanceAsync(core, document, appearance, isCurrent);
        if (!isCurrent()) return;
        await core.ExecuteScriptAsync(BuildSystemAppearanceListener(document));
        if (!isCurrent()) return;
        await core.ExecuteScriptAsync(BuildComponentSelectionScript(_componentSelectionMode, document.Generation));
    }

    private Task<CoreWebView2> EnsureInitializedAsync()
    {
        ThrowIfDisposed();
        return _initializationTask ??= InitializeCoreAsync();
    }

    private static string BuildFixturePageRoutingBootstrap(string fixturePage)
    {
        var script = FixturePageRoutingBootstrapTemplate.Replace(
            "__FIXTURE_PAGE_JSON__",
            JsonSerializer.Serialize(fixturePage),
            StringComparison.Ordinal)
            .Replace(
                "__CODEX_APP_FRAME_SELECTOR__",
                JsonSerializer.Serialize(CodexAdapter.AppFrameSelector),
                StringComparison.Ordinal);
        return script;
    }

    private static async Task<string> GetActualAppearanceAsync(CoreWebView2 core, PreviewDocument document)
    {
        if (document.AppearancePreference is "light" or "dark")
        {
            return document.AppearancePreference;
        }

        const string script = "window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';";
        var result = await core.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(result) is "dark" ? "dark" : "light";
    }

    private async Task ApplyAppearanceAsync(CoreWebView2 core, PreviewDocument document, string appearance, Func<bool> isCurrent)
    {
        if (!isCurrent()) return;
        var actualAppearance = appearance == "dark" ? SkinAppearance.Dark : SkinAppearance.Light;
        ResolvedSkin? resolvedSkin = null;
        if (document.Package is { } package && !document.Options.ShowOriginalBase)
        {
            var scene = document.Options.Scene == "conversation" ? SkinScene.Conversation : SkinScene.Home;
            resolvedSkin = SkinResolver.Resolve(package, actualAppearance, scene, ConvertUserTransparency(document.Options.UserTransparency));
        }

        await core.ExecuteScriptAsync($"document.documentElement.setAttribute('data-theme', {JsonSerializer.Serialize(appearance)});");
        if (!isCurrent()) return;

        string? imageUrl = null;
        if (resolvedSkin?.Background is { ImagePath: { } imagePath } background)
        {
            if (background.ImageBytes is null || !document.Package!.Files.ContainsKey(imagePath))
            {
                throw new InvalidDataException($"皮肤背景资源“{imagePath}”不存在于当前皮肤包中。");
            }

            imageUrl = $"https://{PreviewDocumentBuilder.PreviewHost}/{document.Generation}/skin/{PreviewDocumentBuilder.EncodeRelativePath(imagePath)}";
        }

        var payload = NativeSkinStyleBuilder.Build(resolvedSkin, imageUrl);
        if (!isCurrent()) return;
        await core.ExecuteScriptAsync(NativeSkinStyleBuilder.BuildApplyScript(payload));
        if (isCurrent())
        {
            SetCurrentResolvedSkin(resolvedSkin);
        }
    }

    private static double? ConvertUserTransparency(double? transparency)
    {
        if (!transparency.HasValue)
        {
            return null;
        }

        if (!double.IsFinite(transparency.Value) || transparency.Value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(transparency), "预览面板透明度必须介于 0 到 100 之间。");
        }

        return (100 - transparency.Value) / 100;
    }

    private static string BuildSystemAppearanceListener(PreviewDocument document)
    {
        var preferenceJson = JsonSerializer.Serialize(document.AppearancePreference);
        var generationJson = JsonSerializer.Serialize(document.Generation);
        return $$"""
            (() => {
              const key = "__SKIN_MANAGER_APPEARANCE_LISTENER__";
              const previous = window[key];
              if (previous) previous.media.removeEventListener("change", previous.handler);
              const preference = {{preferenceJson}};
              if (preference !== "system") {
                window[key] = null;
                return;
              }
              const media = window.matchMedia("(prefers-color-scheme: dark)");
              const generation = {{generationJson}};
              const handler = () => {
                const appearance = media.matches ? "dark" : "light";
                document.documentElement.setAttribute("data-theme", appearance);
                window.chrome?.webview?.postMessage(`skin-manager-appearance|${generation}|${appearance}`);
              };
              media.addEventListener("change", handler);
              window[key] = { media, handler };
            })()
            """;
    }

    private static string BuildComponentSelectionScript(bool enabled, string generation)
    {
        var enabledJson = JsonSerializer.Serialize(enabled);
        var generationJson = JsonSerializer.Serialize(generation);
        var accent = Application.Current?.TryFindResource("FocusAccent") is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0xD9, 0xFB, 0x06);
        var accentCssColor = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        return $$"""
            (() => {
              if (location.protocol !== "https:" || location.hostname !== "{{PreviewDocumentBuilder.PreviewHost}}" ||
                  !/^\/[0-9a-f]{32}\/base\//i.test(location.pathname)) return;
              const key = "__SKIN_MANAGER_COMPONENT_PICKER__";
              let picker = window[key];
              if (!picker) {
                const handler = (event) => {
                  if (!picker.enabled) return;
                  const target = event.target instanceof Element ? event.target.closest("[data-skin-part]") : null;
                  const part = target?.getAttribute("data-skin-part");
                  if (!target || !part) return;
                  event.preventDefault();
                  event.stopImmediatePropagation();
                  document.querySelectorAll("[data-skin-editor-picked]").forEach(item => item.removeAttribute("data-skin-editor-picked"));
                  target.setAttribute("data-skin-editor-picked", "true");
                  window.chrome?.webview?.postMessage(`skin-manager-component|${picker.generation}|${part}`);
                };
                document.addEventListener("click", handler, true);
                picker = window[key] = { enabled: false, generation: "", handler };
              }
              picker.enabled = {{enabledJson}};
              picker.generation = {{generationJson}};
              const styleId = "skin-manager-component-picker-style";
              let style = document.getElementById(styleId);
              if (!style) {
                style = document.createElement("style");
                style.id = styleId;
                document.head.appendChild(style);
              }
              style.textContent = picker.enabled
                ? `[data-skin-part] { cursor: crosshair !important; } [data-skin-editor-picked="true"] { outline: 2px solid {{accentCssColor}} !important; outline-offset: -2px !important; }`
                : "";
              if (!picker.enabled) document.querySelectorAll("[data-skin-editor-picked]").forEach(item => item.removeAttribute("data-skin-editor-picked"));
            })()
            """;
    }

    private void SetCurrentResolvedSkin(ResolvedSkin? resolvedSkin)
    {
        if (ReferenceEquals(_currentResolvedSkin, resolvedSkin))
        {
            return;
        }

        _currentResolvedSkin = resolvedSkin;
        CurrentResolvedSkinChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplySystemAppearanceChangeAsync(CoreWebView2 core, PreviewDocument document)
    {
        var requestSequence = Volatile.Read(ref _displayRequestSequence);
        await _displayGate.WaitAsync();
        try
        {
            if (!IsLatestDisplayRequest(requestSequence) || !ReferenceEquals(_currentDocument, document) ||
                document.AppearancePreference != "system")
            {
                return;
            }

            var currentAppearance = await GetActualAppearanceAsync(core, document);
            await ApplyAppearanceAsync(core, document, currentAppearance,
                () => IsLatestDisplayRequest(requestSequence) && ReferenceEquals(_currentDocument, document));
        }
        finally
        {
            _displayGate.Release();
        }
    }

    private async Task<CoreWebView2> InitializeCoreAsync()
    {
        Directory.CreateDirectory(_userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 初始化后没有可用的浏览器控制器。 ");
        var settings = core.Settings;
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Core_WebResourceRequested;
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.DownloadStarting += Core_DownloadStarting;
        return core;
    }

    private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!TryGetLocalPath(args.Request.Uri, out var resourceNamespace, out var generation, out var relativePath))
        {
            BlockedExternalRequests++;
            args.Response = MakeResponse(Array.Empty<byte>(), 403, "Forbidden", "text/plain; charset=utf-8");
            return;
        }

        if (_currentDocument is not { } document || !string.Equals(generation, document.Generation, StringComparison.Ordinal))
        {
            args.Response = MakeResponse(Array.Empty<byte>(), 404, "Not Found", "text/plain; charset=utf-8");
            return;
        }

        if (resourceNamespace.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            ServeBaseResource(args, document, relativePath);
            return;
        }

        if (resourceNamespace.Equals("skin", StringComparison.OrdinalIgnoreCase))
        {
            ServeSkinResource(args, document, relativePath);
            return;
        }

        BlockedExternalRequests++;
        args.Response = MakeResponse(Array.Empty<byte>(), 403, "Forbidden", "text/plain; charset=utf-8");
    }

    private void ServeBaseResource(CoreWebView2WebResourceRequestedEventArgs args, PreviewDocument document, string relativePath)
    {
        if (!IsSafePackagePath(relativePath))
        {
            args.Response = MakeResponse(Array.Empty<byte>(), 404, "Not Found", "text/plain; charset=utf-8");
            return;
        }

        byte[] bytes;
        if (relativePath.Equals(document.BasePackage.Entry, StringComparison.OrdinalIgnoreCase))
        {
            bytes = document.HtmlBytes;
            args.Response = MakeResponse(bytes, 200, "OK", "text/html; charset=utf-8");
        }
        else if (document.BasePackage.Files.TryGetValue(relativePath, out bytes!))
        {
            args.Response = MakeResponse(bytes, 200, "OK", ContentTypeFor(relativePath));
        }
        else
        {
            args.Response = MakeResponse(Array.Empty<byte>(), 404, "Not Found", "text/plain; charset=utf-8");
            return;
        }

        ServedBaseResourceRequests++;
        ServedResourcePaths.Add(relativePath);
        ServedBaseResourcePaths.Add(relativePath);
        if (Path.GetExtension(relativePath).Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            ServedStylesheetRequests++;
        }
    }

    private void ServeSkinResource(CoreWebView2WebResourceRequestedEventArgs args, PreviewDocument document, string relativePath)
    {
        if (document.Package is null || !IsSafePackagePath(relativePath))
        {
            args.Response = MakeResponse(Array.Empty<byte>(), 404, "Not Found", "text/plain; charset=utf-8");
            return;
        }

        if (!document.Package.Files.TryGetValue(relativePath, out var bytes))
        {
            args.Response = MakeResponse(Array.Empty<byte>(), 404, "Not Found", "text/plain; charset=utf-8");
            return;
        }

        args.Response = MakeResponse(bytes, 200, "OK", ContentTypeFor(relativePath));
        ServedResourcePaths.Add(relativePath);
        ServedSkinResourcePaths.Add(relativePath);
        if (Path.GetExtension(relativePath).Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            ServedStylesheetRequests++;
        }
        else
        {
            ServedPackageResourceRequests++;
        }
    }

    private CoreWebView2WebResourceResponse MakeResponse(byte[] bytes, int statusCode, string reasonPhrase, string contentType)
    {
        var content = new MemoryStream(bytes, writable: false);
        return _webView.CoreWebView2!.Environment.CreateWebResourceResponse(
            content,
            statusCode,
            reasonPhrase,
            $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_currentDocument is not { } document ||
            !TryGetLocalPath(args.Uri, out var resourceNamespace, out var generation, out var relativePath) ||
            !resourceNamespace.Equals("base", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(generation, document.Generation, StringComparison.Ordinal) ||
            !relativePath.Equals(document.BasePackage.Entry, StringComparison.OrdinalIgnoreCase))
        {
            BlockedExternalNavigationRequests++;
            args.Cancel = true;
            return;
        }

        _activeNavigationId = args.NavigationId;
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (sender is not CoreWebView2 core || _currentDocument is not { } document)
        {
            return;
        }

        if (!TryGetLocalPath(args.Source, out var resourceNamespace, out var generation, out var relativePath) ||
            !resourceNamespace.Equals("base", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(generation, document.Generation, StringComparison.Ordinal) ||
            !relativePath.Equals(document.BasePackage.Entry, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string message;
        try
        {
            message = args.TryGetWebMessageAsString();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var parts = message.Split('|');
        if (parts.Length == 3 &&
            parts[0].Equals("skin-manager-component", StringComparison.Ordinal) &&
            parts[1].Equals(document.Generation, StringComparison.Ordinal) &&
            _componentSelectionMode && SelectableParts.Contains(parts[2]))
        {
            ComponentSelected?.Invoke(this, new PreviewComponentSelectedEventArgs(parts[2]));
            return;
        }

        if (document.AppearancePreference != "system" ||
            parts.Length != 3 ||
            !parts[0].Equals("skin-manager-appearance", StringComparison.Ordinal) ||
            !parts[1].Equals(document.Generation, StringComparison.Ordinal) ||
            parts[2] is not ("light" or "dark"))
        {
            return;
        }

        _ = ApplySystemAppearanceChangeAsync(core, document);
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_activeNavigationId != args.NavigationId)
        {
            return;
        }

        if (args.IsSuccess)
        {
            _navigationCompletion?.TrySetResult(true);
        }
        else
        {
            _navigationCompletion?.TrySetException(new InvalidOperationException(
                $"原生基础预览文档加载失败（{args.WebErrorStatus}，HTTP {args.HttpStatusCode}）。 "));
        }
    }

    private static void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

    private static void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

    private static bool TryGetLocalPath(string address, out string resourceNamespace, out string generation, out string relativePath)
    {
        resourceNamespace = string.Empty;
        generation = string.Empty;
        relativePath = string.Empty;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(PreviewDocumentBuilder.PreviewHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        }
        catch (UriFormatException)
        {
            return false;
        }

        var firstSlash = decoded.IndexOf('/');
        var secondSlash = firstSlash < 0 ? -1 : decoded.IndexOf('/', firstSlash + 1);
        if (firstSlash <= 0 || secondSlash <= firstSlash + 1 || secondSlash == decoded.Length - 1)
        {
            return false;
        }

        generation = decoded[..firstSlash];
        resourceNamespace = decoded[(firstSlash + 1)..secondSlash];
        relativePath = decoded[(secondSlash + 1)..];
        return generation.Length == 32 && generation.All(Uri.IsHexDigit) &&
               (resourceNamespace.Equals("base", StringComparison.OrdinalIgnoreCase) ||
                resourceNamespace.Equals("skin", StringComparison.OrdinalIgnoreCase)) &&
               IsSafePackagePath(relativePath);
    }

    private static bool IsSafePackagePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/'))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".." && !segment.Any(char.IsControl));
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream"
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class PreviewComponentSelectedEventArgs(string componentId) : EventArgs
{
    public string ComponentId { get; } = componentId;
}
