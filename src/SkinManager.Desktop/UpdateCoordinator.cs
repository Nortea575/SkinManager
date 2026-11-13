using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SkinManager.Updating;

namespace SkinManager.Desktop;

public sealed class UpdateCoordinator : INotifyPropertyChanged, IDisposable
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { WriteIndented = true };
    private readonly IUpdateClient _client;
    private readonly string _settingsPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient? _ownedHttpClient;
    private CancellationTokenSource? _checkCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private UpdateRelease? _release;
    private DownloadedUpdate? _downloadedUpdate;
    private UpdateDownloadProgress? _progress;
    private UpdateCheckState? _checkState;
    private string _statusSource = "尚未检查更新";
    private object?[] _statusArguments = [];
    private readonly string _currentVersionTag;
    private bool _automaticCheckAndDownload = true;
    private bool _includePrereleases;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _automaticCheckStarted;
    private bool _disposed;
    private long _updateChannelRevision;

    public UpdateCoordinator(
        IUpdateClient client,
        string updatesDirectory,
        Version currentVersion,
        HttpClient? ownedHttpClient = null,
        string? currentVersionTag = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesDirectory);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _client = client;
        CurrentVersion = currentVersion;
        _currentVersionTag = NormalizeVersionTag(currentVersionTag, currentVersion);
        UpdatesDirectory = Path.GetFullPath(updatesDirectory);
        CacheDirectory = Path.Combine(UpdatesDirectory, "cache");
        _settingsPath = Path.Combine(UpdatesDirectory, "settings.json");
        _ownedHttpClient = ownedHttpClient;
        LoadPreferences();
        AppLocalization.LanguageChanged += Localization_LanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Version CurrentVersion { get; }

    public string CurrentVersionTag => _currentVersionTag;

    public string CurrentVersionLabel => CurrentVersionTag;

    public bool CurrentVersionIsPrerelease => CurrentVersionTag.Contains("-", StringComparison.Ordinal);

    public string UpdatesDirectory { get; }

    public string CacheDirectory { get; }

    public UpdateRelease? Release
    {
        get => _release;
        private set => SetProperty(ref _release, value);
    }

    public DownloadedUpdate? DownloadedUpdate
    {
        get => _downloadedUpdate;
        private set
        {
            if (SetProperty(ref _downloadedUpdate, value))
            {
                OnPropertyChanged(nameof(HasDownloadedUpdate));
                OnPropertyChanged(nameof(UpdateEntryLabel));
            }
        }
    }

    public UpdateDownloadProgress? Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public UpdateCheckState? CheckState
    {
        get => _checkState;
        private set
        {
            if (SetProperty(ref _checkState, value))
            {
                OnPropertyChanged(nameof(UpdateEntryLabel));
                OnPropertyChanged(nameof(HasAvailableUpdate));
            }
        }
    }

    public string StatusMessage => _statusArguments.Length == 0
        ? AppLocalization.Get(_statusSource)
        : AppLocalization.Format(_statusSource, _statusArguments);

    public bool AutomaticCheckAndDownload
    {
        get => _automaticCheckAndDownload;
        set
        {
            if (SetProperty(ref _automaticCheckAndDownload, value))
            {
                SavePreferences();
            }
        }
    }

    public bool IncludePrereleases
    {
        get => _includePrereleases;
        set
        {
            if (SetProperty(ref _includePrereleases, value))
            {
                InvalidateForUpdateChannelChange();
                SavePreferences();
                OnPropertyChanged(nameof(HasAvailableUpdate));
                OnPropertyChanged(nameof(HasDownloadedUpdate));
                OnPropertyChanged(nameof(UpdateEntryLabel));
            }
        }
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(IsWorking));
            }
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(UpdateEntryLabel));
            }
        }
    }

    public bool IsWorking => IsChecking || IsDownloading;

    public bool HasAvailableUpdate => CheckState == UpdateCheckState.Available && Release is { } release &&
                                     (IncludePrereleases || !release.IsPrerelease);

    public bool HasDownloadedUpdate => DownloadedUpdate is { } downloaded &&
                                       (IncludePrereleases || !downloaded.Release.IsPrerelease);

    public string UpdateEntryLabel => _disposed
        ? AppLocalization.Format("版本 {0}", CurrentVersionLabel)
        : HasDownloadedUpdate
            ? AppLocalization.Format("更新已就绪 · {0}", Release?.DisplayVersion)
            : HasAvailableUpdate
                ? AppLocalization.Format("发现更新 · {0}", Release?.DisplayVersion)
                : IsDownloading
                    ? AppLocalization.Get("正在下载更新…")
                    : IsChecking
                    ? AppLocalization.Get("正在检查更新…")
                    : AppLocalization.Format("版本与更新 · {0}", CurrentVersionLabel);

    public static Version ReadCurrentVersion(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(UpdateCoordinator).Assembly;
        var versionTag = ReadCurrentVersionTag(assembly);
        var numericVersion = versionTag.Split('-', 2)[0];
        if (Version.TryParse(numericVersion, out var version))
        {
            return new Version(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));
        }

        var assemblyVersion = assembly.GetName().Version;
        return new Version(assemblyVersion?.Major ?? 0,
            assemblyVersion?.Minor ?? 0,
            Math.Max(assemblyVersion?.Build ?? 0, 0));
    }

    public static string ReadCurrentVersionTag(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(UpdateCoordinator).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fallback = assembly.GetName().Version ?? new Version(0, 0, 0);
        return NormalizeVersionTag(informational, fallback);
    }

    public void StartAutomatically()
    {
        if (_automaticCheckStarted || !AutomaticCheckAndDownload || _disposed)
        {
            return;
        }

        _automaticCheckStarted = true;
        _ = CheckAsync(downloadAutomatically: true, _lifetime.Token);
    }

    public async Task CheckAsync(bool downloadAutomatically = false, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsWorking)
        {
            return;
        }

        var channelRevision = _updateChannelRevision;
        _checkCancellation?.Dispose();
        var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _checkCancellation = checkCancellation;
        IsChecking = true;
        Progress = null;
        SetStatus("正在检查更新…");
        var shouldDownload = false;
        try
        {
            var result = await _client.CheckAsync(
                new UpdateCheckRequest(CurrentVersion, IncludePrereleases, CurrentVersionTag),
                checkCancellation.Token);
            if (channelRevision != _updateChannelRevision || _disposed)
            {
                return;
            }

            if (!IncludePrereleases && result.Release?.IsPrerelease == true)
            {
                result = new UpdateCheckResult(UpdateCheckState.NotPublished);
            }

            CheckState = result.State;
            Release = result.Release;
            DownloadedUpdate = null;
            switch (result.State)
            {
                case UpdateCheckState.UpToDate:
                    SetStatus("当前已是最新版本 {0}", CurrentVersionLabel);
                    break;
                case UpdateCheckState.Available when result.Release is not null:
                    SetStatus("发现新版本 {0}", result.Release.DisplayVersion);
                    break;
                case UpdateCheckState.NotPublished:
                    SetStatus("尚未发布可用更新");
                    break;
                case UpdateCheckState.PackageNotReady:
                    SetStatus("已发现新版本，但安装包尚未准备好");
                    break;
                default:
                    SetStatus("更新源返回了无法识别的结果");
                    break;
            }
            shouldDownload = downloadAutomatically && AutomaticCheckAndDownload && result.State == UpdateCheckState.Available && result.Release is not null;
        }
        catch (OperationCanceledException) when (checkCancellation.IsCancellationRequested)
        {
            if (channelRevision == _updateChannelRevision && !_disposed)
            {
                SetStatus("更新检查已取消。可以重新检查。");
            }
        }
        catch (Exception exception)
        {
            if (channelRevision == _updateChannelRevision && !_disposed)
            {
                SetStatus("检查更新失败：{0} 可以重试。", exception.Message);
            }
        }
        finally
        {
            IsChecking = false;
            if (ReferenceEquals(_checkCancellation, checkCancellation))
            {
                checkCancellation.Dispose();
                _checkCancellation = null;
            }
        }

        if (shouldDownload && channelRevision == _updateChannelRevision && !_disposed)
        {
            await DownloadAsync(cancellationToken);
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsWorking || Release is not { } release || CheckState != UpdateCheckState.Available ||
            release.IsPrerelease && !IncludePrereleases)
        {
            return;
        }

        var channelRevision = _updateChannelRevision;
        _downloadCancellation?.Dispose();
        var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _downloadCancellation = downloadCancellation;
        var lastProgressUpdate = Stopwatch.GetTimestamp();
        IsDownloading = true;
        Progress = new UpdateDownloadProgress(0, release.Size);
        DownloadedUpdate = null;
        SetStatus("正在下载版本 {0}…", release.DisplayVersion);
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
            if (channelRevision != _updateChannelRevision || _disposed)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(lastProgressUpdate, now);
            var finished = value.TotalBytes > 0 && value.BytesReceived >= value.TotalBytes;
            if (!finished && elapsed < TimeSpan.FromMilliseconds(160))
            {
                return;
            }

            lastProgressUpdate = now;
            Progress = value;
        });

        try
        {
            var downloaded = await _client.DownloadAsync(release, progress, downloadCancellation.Token);
            if (channelRevision != _updateChannelRevision || _disposed)
            {
                return;
            }

            DownloadedUpdate = downloaded;
            SetStatus("版本 {0} 已下载，可以安装。", downloaded.Release.DisplayVersion);
            Progress = new UpdateDownloadProgress(downloaded.Release.Size, downloaded.Release.Size);
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
            if (channelRevision == _updateChannelRevision && !_disposed)
            {
                SetStatus("下载已取消。可以重新下载。");
            }
        }
        catch (Exception exception)
        {
            if (channelRevision == _updateChannelRevision && !_disposed)
            {
                SetStatus("下载失败：{0} 可以重试。", exception.Message);
            }
        }
        finally
        {
            IsDownloading = false;
            if (ReferenceEquals(_downloadCancellation, downloadCancellation))
            {
                _downloadCancellation.Dispose();
                _downloadCancellation = null;
            }
        }
    }

    public void CancelDownload()
    {
        _downloadCancellation?.Cancel();
    }

    public async Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var channelRevision = _updateChannelRevision;
        EnsureUpdateIsCurrentForInstallation(update, channelRevision);
        await _client.ValidateAsync(update, cancellationToken);
        EnsureUpdateIsCurrentForInstallation(update, channelRevision);
    }

    public void ReportInstallFailure(string message)
    {
        SetStatus("安装未启动：{0}", message);
    }

    public void ReportInstallReady()
    {
        if (DownloadedUpdate is { } update)
        {
            SetStatus("版本 {0} 已下载，可以安装。", update.Release.DisplayVersion);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _checkCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        _ownedHttpClient?.Dispose();
        _checkCancellation?.Dispose();
        _checkCancellation = null;
        _lifetime.Dispose();
        AppLocalization.LanguageChanged -= Localization_LanguageChanged;
        OnPropertyChanged(nameof(UpdateEntryLabel));
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void LoadPreferences()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var preferences = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(_settingsPath));
                if (preferences is not null)
                {
                    _automaticCheckAndDownload = preferences.AutomaticCheckAndDownload;
                    _includePrereleases = preferences.IncludePrereleases;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _automaticCheckAndDownload = true;
            _includePrereleases = false;
        }
    }

    private void SavePreferences()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(
                new UpdatePreferences
                {
                    AutomaticCheckAndDownload = AutomaticCheckAndDownload,
                    IncludePrereleases = IncludePrereleases
                }, SettingsJsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("更新偏好无法保存：{0}", exception.Message);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetStatus(string source, params object?[] arguments)
    {
        var previousMessage = StatusMessage;
        _statusSource = source;
        _statusArguments = arguments;
        if (!string.Equals(previousMessage, StatusMessage, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(UpdateEntryLabel));
        }
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(UpdateEntryLabel));
    }

    private void InvalidateForUpdateChannelChange()
    {
        _updateChannelRevision++;
        _checkCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        Release = null;
        DownloadedUpdate = null;
        CheckState = null;
        Progress = null;
        SetStatus("更新通道已更改，请重新检查更新。");
    }

    private void EnsureUpdateIsCurrentForInstallation(DownloadedUpdate update, long channelRevision)
    {
        if (_disposed || channelRevision != _updateChannelRevision ||
            !ReferenceEquals(DownloadedUpdate, update) ||
            update.Release.IsPrerelease && !IncludePrereleases)
        {
            throw new InvalidOperationException(AppLocalization.Get("此更新已不属于当前更新通道，请重新检查并下载。"));
        }
    }

    private static string NormalizeVersionTag(string? versionTag, Version fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(versionTag)
            ? fallback.ToString(3)
            : versionTag.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        var prereleaseStart = candidate.IndexOf("-", StringComparison.Ordinal);
        var numericPart = prereleaseStart >= 0 ? candidate[..prereleaseStart] : candidate;
        if (!Version.TryParse(numericPart, out var parsed) || parsed.Revision >= 0)
        {
            candidate = fallback.ToString(3);
            prereleaseStart = -1;
            parsed = fallback;
        }

        var normalized = $"{parsed.Major}.{Math.Max(parsed.Minor, 0)}.{Math.Max(parsed.Build, 0)}";
        return prereleaseStart >= 0 ? normalized + candidate[prereleaseStart..] : normalized;
    }

    private sealed record UpdatePreferences
    {
        public bool AutomaticCheckAndDownload { get; init; } = true;

        public bool IncludePrereleases { get; init; }
    }
}
