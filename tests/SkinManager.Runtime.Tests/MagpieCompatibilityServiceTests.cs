using SkinManager.Runtime;

internal static class MagpieCompatibilityServiceTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SkinManager-Magpie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installedPath = Path.Combine(root, "magpie.exe");
            await File.WriteAllBytesAsync(installedPath, []);

            var installedPlatform = new FakeMagpiePlatform
            {
                DefaultPath = installedPath,
                RunningPath = installedPath
            };
            installedPlatform.Metadata[installedPath] = OfficialMetadata("0.1.3237");
            var installed = new MagpieCompatibilityService(installedPlatform).Inspect();
            Assert(installed.IsInstalled && installed.IsRunning && installed.CanLaunch &&
                installed.ExecutablePath == Path.GetFullPath(installedPath) && installed.ProductVersion == "0.1.3237" &&
                installed.Support == MagpieCompatibilitySupport.Unsupported,
                "the verified v0.1.3237 install is detected with its path, running state, version, and unsupported status");
            Assert(installed.Evidence.Contains("v0.1.3237", StringComparison.Ordinal),
                "the known release includes the exact version evidence");

            await new MagpieCompatibilityService(installedPlatform).LaunchAsync();
            Assert(installedPlatform.LaunchRequests.SequenceEqual([Path.GetFullPath(installedPath)]),
                "the explicit launch workflow starts only the verified executable path");

            var portablePath = Path.Combine(root, "portable-magpie.exe");
            await File.WriteAllBytesAsync(portablePath, []);
            var portablePlatform = new FakeMagpiePlatform();
            portablePlatform.Metadata[portablePath] = OfficialMetadata("0.1.3238");
            var portable = new MagpieCompatibilityService(portablePlatform).Inspect(portablePath);
            Assert(portable.IsInstalled && !portable.IsRunning && portable.CanLaunch &&
                portable.Support == MagpieCompatibilitySupport.Unverified &&
                portable.ExecutablePath == Path.GetFullPath(portablePath),
                "a manually selected portable version is recognized but left unverified");

            var runningPortablePlatform = new FakeMagpiePlatform
            {
                RunningPath = portablePath,
                DefaultPath = installedPath
            };
            runningPortablePlatform.Metadata[portablePath] = OfficialMetadata("0.1.3238");
            runningPortablePlatform.Metadata[installedPath] = OfficialMetadata("0.1.3237");
            var runningPortable = new MagpieCompatibilityService(runningPortablePlatform).Inspect();
            Assert(runningPortable.IsInstalled && runningPortable.IsRunning &&
                runningPortable.ExecutablePath == Path.GetFullPath(portablePath) &&
                runningPortable.Support == MagpieCompatibilitySupport.Unverified,
                "a running portable installation is found before the standard installation path");

            var otherProductPath = Path.Combine(root, "other-magpie.exe");
            await File.WriteAllBytesAsync(otherProductPath, []);
            var otherProductPlatform = new FakeMagpiePlatform();
            otherProductPlatform.Metadata[otherProductPath] = OfficialMetadata("0.1.3237") with
            {
                CompanyName = "Blinue",
                ProductName = "Magpie"
            };
            var otherProduct = new MagpieCompatibilityService(otherProductPlatform).Inspect(otherProductPath);
            Assert(!otherProduct.IsInstalled && !otherProduct.CanLaunch &&
                otherProduct.Support == MagpieCompatibilitySupport.InvalidExecutable &&
                otherProductPlatform.LaunchRequests.Count == 0,
                "a different Magpie product is not mistaken for yetone/magpie");

            var invalidSelection = new MagpieCompatibilityService(installedPlatform).Inspect(Path.Combine(root, "missing.exe"));
            Assert(invalidSelection.Support == MagpieCompatibilitySupport.InvalidExecutable && !invalidSelection.CanLaunch,
                "a missing manual selection is not replaced by a detected default executable");

            var empty = new MagpieCompatibilityService(new FakeMagpiePlatform()).Inspect();
            Assert(empty.Support == MagpieCompatibilitySupport.NotInstalled && !empty.IsInstalled && !empty.CanLaunch,
                "an absent executable reports not installed");

            var unsupportedPlatform = new FakeMagpiePlatform { IsWindows = false, DefaultPath = installedPath };
            var unsupported = new MagpieCompatibilityService(unsupportedPlatform).Inspect();
            Assert(unsupported.Support == MagpieCompatibilitySupport.UnsupportedPlatform && !unsupported.CanLaunch,
                "non-Windows checks fail without looking up or launching the executable");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellation = await CaptureAsync(() => new MagpieCompatibilityService(installedPlatform)
                .LaunchAsync(cancellationToken: cancelled.Token));
            Assert(cancellation is OperationCanceledException && installedPlatform.LaunchRequests.Count == 1,
                "a cancelled explicit launch does not call the platform launcher");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MagpieExecutableMetadata OfficialMetadata(string? version) => new(
        "magpie",
        "magpie",
        "magpie.exe",
        version);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
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
            throw new InvalidOperationException("Magpie compatibility test failed: " + message);
    }

    private sealed class FakeMagpiePlatform : IMagpieCompatibilityPlatform
    {
        public bool IsWindows { get; init; } = true;
        public string? DefaultPath { get; init; }
        public string? RunningPath { get; init; }
        public Dictionary<string, MagpieExecutableMetadata> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> LaunchRequests { get; } = [];

        public string? FindDefaultExecutable() => RunningPath ?? DefaultPath;

        public bool FileExists(string path) => File.Exists(path);

        public MagpieExecutableMetadata ReadMetadata(string path) => Metadata[path];

        public bool IsRunning() => RunningPath is not null && File.Exists(RunningPath);

        public Task LaunchAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchRequests.Add(path);
            return Task.CompletedTask;
        }
    }
}
