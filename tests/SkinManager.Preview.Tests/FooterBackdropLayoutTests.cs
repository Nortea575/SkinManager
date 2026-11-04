using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SkinManager.Core;
using SkinManager.Rendering;

namespace SkinManager.Preview.Tests;

internal static class FooterBackdropLayoutTests
{
    public static async Task VerifyAsync(CoreWebView2 core, SkinPackage package)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(package);

        var appearance = package.Appearance is SkinAppearance.Light or SkinAppearance.Dark
            ? package.Appearance
            : SkinAppearance.Dark;
        var resolved = SkinResolver.Resolve(package, appearance, SkinScene.Conversation);
        if (resolved.Background is null)
        {
            throw new InvalidOperationException("Footer backdrop layout verification requires a skin with a background so the native surface rules are emitted.");
        }

        var css = NativeSkinStyleBuilder.Build(resolved, imageUrl: null).Css;
        if (!css.Contains("[data-thread-scroll-footer=\"true\"] > ._background_18hic_2._surface_18hic_2", StringComparison.Ordinal)
            || !css.Contains(".thread-scroll-container ._background_18hic_2._fade_18hic_2", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NativeSkinStyleBuilder did not emit the footer surface and fade rules.");
        }

        var theme = appearance == SkinAppearance.Light ? "light" : "dark";
        await NavigateToFixtureAsync(core, FixtureHtml.Replace("__THEME__", theme, StringComparison.Ordinal)).ConfigureAwait(true);
        var scrollPosition = await ReadStringAsync(core, "getComputedStyle(document.querySelector('.thread-scroll-container')).position").ConfigureAwait(true);
        if (!string.Equals(scrollPosition, "static", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The offline fixture must preserve the native static scrollbox (computed: {scrollPosition}).");
        }
        var initialGutter = await ReadStringAsync(core, "getComputedStyle(document.querySelector('.thread-scroll-container')).scrollbarGutter").ConfigureAwait(true);
        if (!string.Equals(initialGutter, "stable both-edges", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The offline native fixture did not retain scrollbar-gutter:stable both-edges (computed: {initialGutter}).");
        }
        var nativeFadeTranslate = await ReadStringAsync(core, "getComputedStyle(document.getElementById('footer-fade')).translate").ConfigureAwait(true);
        if (!string.Equals(nativeFadeTranslate, "none", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The native fade must inherit its shift from its ancestor (computed own translate: {nativeFadeTranslate}).");
        }

        foreach (var width in new[] { 1000, 620 })
        {
            await SetLayoutAsync(core, width, 0).ConfigureAwait(true);
            var nativeNormal = await ReadLayoutAsync(core).ConfigureAwait(true);
            AssertNear(nativeNormal.Footer.Left, nativeNormal.Work.Left, "The native footer is positioned against the outer work area, outside the scrollbox gutter.");
            AssertNear(nativeNormal.Footer.Right, nativeNormal.Work.Right, "The native footer keeps the full outer work-area width.");
            AssertNear(nativeNormal.Surface.Left, nativeNormal.Work.Left + 16, "Unfixed footer surface has the measured 16 CSS px left inset.");
            AssertNear(nativeNormal.Work.Right - nativeNormal.Surface.Right, 16, "Unfixed footer surface has the measured 16 CSS px right inset.");
            AssertNear(nativeNormal.Fade.Left, nativeNormal.ScrollContent.Left, "The native fade starts at the scroll content edge, inside the gutter.");
            AssertNear(nativeNormal.Fade.Right, nativeNormal.ScrollContent.Right, "The native fade ends at the scroll content edge, inside the gutter.");

            await SetLayoutAsync(core, width, -154).ConfigureAwait(true);
            var nativeShifted = await ReadLayoutAsync(core).ConfigureAwait(true);
            AssertNear(nativeShifted.Footer.Left - nativeNormal.Footer.Left, -154, "The native footer follows --thread-content-x.");
            AssertNear(nativeShifted.ScrollContent.Left - nativeNormal.ScrollContent.Left, -154, "The native scroll-content wrapper follows --thread-content-x.");
            AssertNear(nativeShifted.Composer.Left - nativeNormal.Composer.Left, -154, "The native input remains shifted with the footer.");
            AssertNear(nativeShifted.Message.Left - nativeNormal.Message.Left, -154, "The native transcript remains shifted.");
            AssertNear(nativeShifted.Fade.Left - nativeNormal.Fade.Left, -154, "The native fade follows its shifted ancestor before the skin rule is applied.");
            AssertNear(nativeShifted.Fade.Right - nativeNormal.Fade.Right, -154, "The native fade preserves its gutter-bound width while its ancestor moves.");
            AssertNear(nativeShifted.Fade.Width, nativeNormal.Fade.Width, "The native fade width stays limited to the scroll content box.");
            AssertNear(nativeShifted.Work.Right - nativeShifted.Surface.Right, 170, "The unmodified footer shows the observed 170 CSS px right gap after a -154 px shift.");
            AssertNear(Math.Max(nativeShifted.Work.Left, nativeShifted.Surface.Left), nativeShifted.Work.Left,
                "The workspace clip masks the surface's left overhang.");

            await SetSkinCssAsync(core, css).ConfigureAwait(true);
            await SetLayoutAsync(core, width, 0).ConfigureAwait(true);
            var skinnedNormal = await ReadLayoutAsync(core).ConfigureAwait(true);
            AssertNear(skinnedNormal.Surface.Left, skinnedNormal.Work.Left, "The skin rule fills the workspace edge at zero shift.");
            AssertNear(skinnedNormal.Surface.Right, skinnedNormal.Work.Right, "The skin rule fills the workspace right edge at zero shift.");
            AssertUnchanged(nativeNormal.Fade, skinnedNormal.Fade, "At zero shift the fade keeps its native scroll-content bounds.");
            AssertUnchanged(nativeNormal.Footer, skinnedNormal.Footer, "The native footer geometry is unchanged at zero shift.");
            AssertUnchanged(nativeNormal.Composer, skinnedNormal.Composer, "The native composer geometry is unchanged at zero shift.");
            AssertUnchanged(nativeNormal.Message, skinnedNormal.Message, "The native transcript geometry is unchanged at zero shift.");
            AssertUnchanged(nativeNormal.OtherSurface, skinnedNormal.OtherSurface, "A neighboring panel surface is unchanged at zero shift.");

            await SetLayoutAsync(core, width, -154).ConfigureAwait(true);
            var skinnedShifted = await ReadLayoutAsync(core).ConfigureAwait(true);
            AssertNear(skinnedShifted.Surface.Left, skinnedShifted.Work.Left, "At -154 px the footer surface remains flush with the workspace left edge.");
            AssertNear(skinnedShifted.Surface.Right, skinnedShifted.Work.Right, "At -154 px the footer surface remains flush with the workspace right edge.");
            AssertUnchanged(nativeNormal.Fade, skinnedShifted.Fade, "At -154 px the fade returns to its unshifted native scroll-content bounds.");
            AssertUnchanged(nativeShifted.Footer, skinnedShifted.Footer, "The native footer continues to follow its original content shift.");
            AssertUnchanged(nativeShifted.Composer, skinnedShifted.Composer, "The native input continues to follow its original content shift.");
            AssertUnchanged(nativeShifted.Message, skinnedShifted.Message, "The native transcript continues to follow its original content shift.");
            AssertUnchanged(nativeShifted.OtherSurface, skinnedShifted.OtherSurface, "The neighboring panel surface is not widened or translated.");

            await SetSkinCssAsync(core, string.Empty).ConfigureAwait(true);
        }

        Console.WriteLine("通过：footer surface 在 0、-154 CSS px 与宽/窄布局下贴合工作区；fade 保持滚动内容区基线，正文和输入框保留原生位移。");
    }

    private static async Task NavigateToFixtureAsync(CoreWebView2 core, string html)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = null!;
        handler = (_, args) =>
        {
            core.NavigationCompleted -= handler;
            if (args.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new InvalidOperationException($"Offline footer fixture navigation failed: {args.WebErrorStatus}."));
        };

        core.NavigationCompleted += handler;
        try
        {
            core.NavigateToString(html);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        }
        finally
        {
            core.NavigationCompleted -= handler;
        }
    }

    private static async Task<string> ReadStringAsync(CoreWebView2 core, string expression)
    {
        var result = await core.ExecuteScriptAsync($"(()=>({expression}))()").ConfigureAwait(true);
        return JsonSerializer.Deserialize<string>(result)
            ?? throw new InvalidDataException("The offline footer fixture did not return a string value.");
    }

    private static async Task SetLayoutAsync(CoreWebView2 core, int width, int contentShift)
    {
        var script = $"(() => {{ const app = document.getElementById('app-shell'); const layout = document.getElementById('thread-scroll-layout'); app.style.setProperty('--work-width', '{width}px'); layout.style.setProperty('--thread-content-x', '{contentShift}px'); return true; }})()";
        await core.ExecuteScriptAsync(script).ConfigureAwait(true);
    }

    private static async Task SetSkinCssAsync(CoreWebView2 core, string css)
    {
        var cssLiteral = JsonSerializer.Serialize(css);
        await core.ExecuteScriptAsync($"document.getElementById('skin-manager-css').textContent = {cssLiteral}; true;").ConfigureAwait(true);
    }

    private static async Task<LayoutSnapshot> ReadLayoutAsync(CoreWebView2 core)
    {
        var result = await core.ExecuteScriptAsync("""
            (() => {
              const box = (element) => {
                const rect = element.getBoundingClientRect();
                return { left: rect.left, right: rect.right, width: rect.width, top: rect.top, bottom: rect.bottom };
              };
              return {
                work: box(document.getElementById('chat-workspace')),
                footer: box(document.querySelector('[data-thread-scroll-footer="true"]')),
                surface: box(document.getElementById('footer-surface')),
                fade: box(document.getElementById('footer-fade')),
                scrollContent: box(document.querySelector('.scroll-content')),
                composer: box(document.getElementById('composer-input')),
                message: box(document.getElementById('transcript-message')),
                otherSurface: box(document.getElementById('other-surface'))
              };
            })()
            """).ConfigureAwait(true);

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        return new LayoutSnapshot(
            ReadBox(root.GetProperty("work")),
            ReadBox(root.GetProperty("footer")),
            ReadBox(root.GetProperty("surface")),
            ReadBox(root.GetProperty("fade")),
            ReadBox(root.GetProperty("scrollContent")),
            ReadBox(root.GetProperty("composer")),
            ReadBox(root.GetProperty("message")),
            ReadBox(root.GetProperty("otherSurface")));
    }

    private static Box ReadBox(JsonElement element) => new(
        element.GetProperty("left").GetDouble(),
        element.GetProperty("right").GetDouble(),
        element.GetProperty("width").GetDouble(),
        element.GetProperty("top").GetDouble(),
        element.GetProperty("bottom").GetDouble());

    private static void AssertUnchanged(Box expected, Box actual, string message)
    {
        AssertNear(actual.Left, expected.Left, message + " (left)");
        AssertNear(actual.Right, expected.Right, message + " (right)");
        AssertNear(actual.Width, expected.Width, message + " (width)");
        AssertNear(actual.Top, expected.Top, message + " (top)");
        AssertNear(actual.Bottom, expected.Bottom, message + " (bottom)");
    }

    private static void AssertNear(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 0.75)
        {
            throw new InvalidOperationException($"{message} Expected {expected:0.##} CSS px, got {actual:0.##} CSS px.");
        }
    }

    private sealed record Box(double Left, double Right, double Width, double Top, double Bottom);

    private sealed record LayoutSnapshot(
        Box Work,
        Box Footer,
        Box Surface,
        Box Fade,
        Box ScrollContent,
        Box Composer,
        Box Message,
        Box OtherSurface);

    private const string FixtureHtml = """
        <!doctype html>
        <html data-theme="__THEME__">
          <head>
            <meta charset="utf-8">
            <style id="skin-manager-css"></style>
            <style>
              html, body { margin: 0; width: 1200px; height: 700px; overflow: hidden; background: #13202b; font: 14px/1.4 system-ui,sans-serif; }
              #app-shell { position: relative; width: 1200px; height: 700px; --work-width: 1000px; }
              #chat-workspace { position: relative; width: var(--work-width); height: 600px; overflow: hidden; background: #13202b; }
              #thread-scroll-layout { position: relative; width: 100%; height: 100%; --thread-content-x: 0px; }
              .thread-scroll-container { position: static; width: 100%; height: 100%; overflow-y: auto; scrollbar-gutter: stable both-edges; }
              .scroll-content { position: relative; width: 100%; min-height: 1100px; translate: var(--thread-content-x, 0px) 0; }
              .transcript { position: relative; min-height: 1100px; }
              #transcript-message { position: absolute; left: 160px; top: 80px; white-space: nowrap; color: #e5efe9; }
              [data-thread-scroll-footer="true"] { position: absolute; inset-inline: 0; bottom: 0; height: 114px; translate: var(--thread-content-x, 0px) 0; pointer-events: none; }
              [data-thread-scroll-footer="true"] > ._background_18hic_2._surface_18hic_2 { position: absolute; inset-inline: 16px; top: -32px; bottom: 0; background: #13202b; }
              #composer-input { position: absolute; left: 0; right: 0; bottom: 10px; width: 480px; height: 48px; margin: 0 auto; background: #182a35; border-radius: 24px; }
              .scroll-content > ._background_18hic_2._fade_18hic_2 { position: absolute; inset-inline: 0; bottom: 114px; height: 32px; background: linear-gradient(transparent, #13202b); }
              #other-panel { position: absolute; left: var(--work-width); top: 0; width: 200px; height: 600px; overflow: hidden; background: #24323a; }
              #other-panel > ._background_18hic_2._surface_18hic_2 { position: absolute; inset-inline: 16px; top: 0; bottom: 0; background: #13202b; }
            </style>
          </head>
          <body>
            <main id="app-shell">
              <section id="chat-workspace">
                <div id="thread-scroll-layout" class="group/thread-scroll-layout">
                  <div class="thread-scroll-container">
                    <div class="scroll-content">
                      <div class="transcript"><div id="transcript-message">The transcript follows the native content shift.</div></div>
                      <div id="footer-fade" class="_background_18hic_2 _fade_18hic_2"></div>
                    </div>
                  </div>
                  <footer data-thread-scroll-footer="true" class="_footer_1q9qf_2 _shiftWithTranscript_1q9qf_2">
                    <div id="footer-surface" class="_background_18hic_2 _surface_18hic_2"></div>
                    <div id="composer-input"></div>
                  </footer>
                </div>
              </section>
              <aside id="other-panel"><div id="other-surface" class="_background_18hic_2 _surface_18hic_2"></div></aside>
            </main>
          </body>
        </html>
        """;
}
