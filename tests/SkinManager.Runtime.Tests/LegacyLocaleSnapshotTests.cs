using SkinManager.Runtime;

internal static class LegacyLocaleSnapshotTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SkinManager-LegacyLocaleSnapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestSnapshotPersistsOriginalNull(root);
            TestCorruptSnapshotIsNotReplacedAndClearIsIdempotent(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestSnapshotPersistsOriginalNull(string root)
    {
        var path = Path.Combine(root, "locale-snapshot.json");
        var firstStore = new LocaleOverrideSnapshotStore(path);
        var captured = firstStore.CaptureIfMissing(originalValue: null);
        Assert(captured.OriginalValue is null && captured.LastWrittenValue == "zh-CN",
            "a legacy snapshot records an originally absent locale value");

        var secondStore = new LocaleOverrideSnapshotStore(path);
        var loaded = secondStore.Load();
        Assert(loaded is { OriginalValue: null, LastWrittenValue: "zh-CN" },
            "an originally absent locale value survives loading through a new store instance");

        var laterCapture = secondStore.CaptureIfMissing("zh-CN");
        Assert(laterCapture.OriginalValue is null &&
               new LocaleOverrideSnapshotStore(path).Load()?.OriginalValue is null,
            "a later capture cannot replace the original snapshot with zh-CN");
    }

    private static void TestCorruptSnapshotIsNotReplacedAndClearIsIdempotent(string root)
    {
        var path = Path.Combine(root, "corrupt-locale-snapshot.json");
        const string invalidJson = "{ corrupted snapshot";
        File.WriteAllText(path, invalidJson);

        var store = new LocaleOverrideSnapshotStore(path);
        var error = Capture(() => store.CaptureIfMissing("en-US"));
        Assert(error is System.Text.Json.JsonException && File.ReadAllText(path) == invalidJson,
            "a corrupt locale snapshot is reported and is not silently replaced");

        store.Clear();
        store.Clear();
        Assert(!File.Exists(path), "clearing a locale snapshot is safe to repeat");
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Legacy locale snapshot test failed: " + message);
    }
}
