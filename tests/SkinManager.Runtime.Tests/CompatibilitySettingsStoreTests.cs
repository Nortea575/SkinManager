using SkinManager.Runtime;

internal static class CompatibilitySettingsStoreTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "SkinManager-CompatibilitySettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestSettingsRoundTrip(root);
            TestInvalidSettingsRemainUntouched(root);
            TestFailedAtomicSavePreservesPriorValue(root);
            TestSnapshotPersistsOriginalNull(root);
            TestCorruptSnapshotIsNotReplacedAndClearIsIdempotent(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void TestSettingsRoundTrip(string root)
    {
        var path = Path.Combine(root, "settings-round-trip.json");
        var firstStore = new CompatibilitySettingsStore(path);
        Assert(!firstStore.Load().ForceSimplifiedChinese && !File.Exists(path),
            "missing settings default to disabled without creating a file");

        firstStore.Save(new RuntimeCompatibilityOptions(ForceSimplifiedChinese: true));
        Assert(new CompatibilitySettingsStore(path).Load().ForceSimplifiedChinese,
            "enabled compatibility settings survive loading through a new store instance");

        new CompatibilitySettingsStore(path).Save(new RuntimeCompatibilityOptions(ForceSimplifiedChinese: false));
        Assert(!new CompatibilitySettingsStore(path).Load().ForceSimplifiedChinese,
            "disabled compatibility settings survive loading through a new store instance");
    }

    private static void TestInvalidSettingsRemainUntouched(string root)
    {
        var path = Path.Combine(root, "invalid-settings.json");
        const string invalidJson = "{ this is not valid JSON";
        File.WriteAllText(path, invalidJson);

        var error = Capture(() => new CompatibilitySettingsStore(path).Load());
        Assert(error is System.Text.Json.JsonException && File.ReadAllText(path) == invalidJson,
            "invalid settings fail explicitly and the original file is not overwritten");
    }

    private static void TestFailedAtomicSavePreservesPriorValue(string root)
    {
        var directory = Path.Combine(root, "locked-save");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        new CompatibilitySettingsStore(path).Save(new RuntimeCompatibilityOptions(ForceSimplifiedChinese: true));

        Exception? error;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            error = Capture(() => new CompatibilitySettingsStore(path)
                .Save(new RuntimeCompatibilityOptions(ForceSimplifiedChinese: false)));
            Assert(error is IOException or UnauthorizedAccessException,
                "a locked destination makes the atomic replacement fail");
            Assert(!Directory.EnumerateFiles(directory, ".settings.json.*.tmp").Any(),
                "a failed atomic replacement removes its temporary file");
        }

        Assert(new CompatibilitySettingsStore(path).Load().ForceSimplifiedChinese,
            "a failed atomic replacement leaves the prior valid setting intact");
    }

    private static void TestSnapshotPersistsOriginalNull(string root)
    {
        var path = Path.Combine(root, "locale-snapshot.json");
        var firstStore = new LocaleOverrideSnapshotStore(path);
        var captured = firstStore.CaptureIfMissing(originalValue: null);
        Assert(captured.OriginalValue is null && captured.LastWrittenValue == "zh-CN",
            "a missing locale override snapshot records an originally absent value");

        var secondStore = new LocaleOverrideSnapshotStore(path);
        var loaded = secondStore.Load();
        Assert(loaded is { OriginalValue: null, LastWrittenValue: "zh-CN" },
            "the originally absent locale value survives loading through a new store instance");

        var laterCapture = secondStore.CaptureIfMissing("zh-CN");
        Assert(laterCapture.OriginalValue is null &&
               new LocaleOverrideSnapshotStore(path).Load()?.OriginalValue is null,
            "a later window cannot replace the first snapshot with the forced locale value");
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
            throw new InvalidOperationException("Compatibility settings store test failed: " + message);
    }
}
