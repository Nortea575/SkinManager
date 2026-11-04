internal static class NativeFooterBackdropTests
{
    public static void VerifyCss(string css)
    {
        const string surfaceRule = ":root[data-theme] [data-thread-scroll-footer=\"true\"] > ._background_18hic_2._surface_18hic_2 {inset-inline:0 !important;translate:calc(0px - var(--thread-content-x, 0px)) 0 !important;}";
        const string fadeRule = ":root[data-theme] .thread-scroll-container ._background_18hic_2._fade_18hic_2 {inset-inline:0 !important;translate:calc(0px - var(--thread-content-x, 0px)) 0 !important;}";

        Require(css.Contains(surfaceRule, StringComparison.Ordinal),
            "The footer surface fills its chat footer and cancels only the transcript horizontal shift.");
        Require(css.Contains(fadeRule, StringComparison.Ordinal),
            "The transcript footer fade stays aligned with the unshifted backdrop.");
        Require(!css.Contains("._background_18hic_2 {", StringComparison.Ordinal),
            "Background position overrides are not applied to every native background surface.");
    }

    public static void VerifyRuntimeUpgrade(string runtimeScript)
    {
        Require(runtimeScript.Contains("existing.appearanceVersion === 4", StringComparison.Ordinal) &&
                runtimeScript.Contains("appearanceVersion: 4", StringComparison.Ordinal),
            "The shared fill engine and footer behavior use runtime capability version 4.");

        var previousRuntimeRemoval = runtimeScript.IndexOf("existing.remove?.();", StringComparison.Ordinal);
        var nextRuntimeStart = runtimeScript.IndexOf("manager.start();", StringComparison.Ordinal);
        Require(previousRuntimeRemoval >= 0 && nextRuntimeStart > previousRuntimeRemoval,
            "The old runtime is removed before the upgraded runtime starts.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
