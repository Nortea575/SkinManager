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
    private CancellationTokenSource? _downloadCancellation;
    private UpdateRelease? _release;
    private DownloadedUpdate? _downloadedUpdate;
    private UpdateDownloadProgress? _progress;
    private UpdateCheckState? _checkState;
    private string _statusMessage = "尚未检查更新";
    private bool _automaticCheckAndDownload = true;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _automaticCheckStarted;
    private bool _disposed;

    public UpdateCoordinator(
        IUpdateClient client,
        string updatesDirectory,
        Version currentVersion,
        HttpClient? ownedHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesDirectory);
        ArgumentNullException.ThrowIfNull(currentVersion);

        _client = client;
        CurrentVersion = currentVersion;
        UpdatesDirectory = Path.GetFullPath(updatesDirectory);
        CacheDirectory = Path.Combine(UpdatesDirectory, "cache");
        _settingsPath = Path.Combine(UpdatesDirectory, "settings.json");
        _ownedHttpClient = ownedHttpClient;
        LoadPreferences();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Version CurrentVersion { get; }

    public string CurrentVersionLabel => CurrentVersion.ToString(3);

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

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(UpdateEntryLabel));
            }
        }
    }

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

    public bool HasAvailableUpdate => CheckState == UpdateCheckState.Available && Release is not null;

    public bool HasDownloadedUpdate => DownloadedUpdate is not null;

    public string UpdateEntryLabel => _disposed
        ? $"版本 {CurrentVersionLabel}"
        : HasDownloadedUpdate
            ? $"更新已就绪 · {Release?.Version.ToString(3)}"
            : HasAvailableUpdate
                ? $"发现更新 · {Release?.Version.ToString(3)}"
                : IsDownloading
                    ? "正在下载更新…"
                    : IsChecking
                    ? "正在检查更新…"
                    : $"版本与更新 · {CurrentVersionLabel}";

    public static Version ReadCurrentVersion(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(UpdateCoordinator).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var versionText = informational?.Split('+', 2)[0];
        return Version.TryParse(versionText, out var version)
            ? new Version(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0))
            : new Version(assembly.GetName().Version?.Major ?? 0,
                assembly.GetName().Version?.Minor ?? 0,
                Math.Max(assembly.GetName().Version?.Build ?? 0, 0));
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

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        IsChecking = true;
        Progress = null;
        StatusMessage = "正在检查更新…";
        var shouldDownload = false;
        try
        {
            var result = await _client.CheckAsync(CurrentVersion, linkedCancellation.Token);
            CheckState = result.State;
            Release = result.Release;
            DownloadedUpdate = null;
            StatusMessage = result.State switch
            {
                UpdateCheckState.UpToDate => $"当前已是最新版本 {CurrentVersionLabel}",
                UpdateCheckState.Available when result.Release is not null => $"发现新版本 {result.Release.Version.ToString(3)}",
                UpdateCheckState.NotPublished => "尚未发布可用更新",
                UpdateCheckState.PackageNotReady => "已发现新版本，但安装包尚未准备好",
                _ => "更新源返回了无法识别的结果"
            };
            shouldDownload = downloadAutomatically && AutomaticCheckAndDownload && result.State == UpdateCheckState.Available && result.Release is not null;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            StatusMessage = "更新检查已取消。可以重新检查。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"检查更新失败：{exception.Message} 可以重试。";
        }
        finally
        {
            IsChecking = false;
        }

        if (shouldDownload && !_disposed)
        {
            await DownloadAsync(cancellationToken);
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsWorking || Release is null || CheckState != UpdateCheckState.Available)
        {
            return;
        }

        _downloadCancellation?.Dispose();
        _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var downloadCancellation = _downloadCancellation;
        var lastProgressUpdate = Stopwatch.GetTimestamp();
        IsDownloading = true;
        Progress = new UpdateDownloadProgress(0, Release.Size);
        DownloadedUpdate = null;
        StatusMessage = $"正在下载版本 {Release.Version.ToString(3)}…";
        var progress = new Progress<UpdateDownloadProgress>(value =>
        {
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
            var downloaded = await _client.DownloadAsync(Release, progress, downloadCancellation.Token);
            DownloadedUpdate = downloaded;
            StatusMessage = $"版本 {downloaded.Release.Version.ToString(3)} 已下载，可以安装。";
            Progress = new UpdateDownloadProgress(downloaded.Release.Size, downloaded.Release.Size);
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
            StatusMessage = "下载已取消。可以重新下载。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"下载失败：{exception.Message} 可以重试。";
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

    public Task ValidateAsync(DownloadedUpdate update, CancellationToken cancellationToken = default) =>
        _client.ValidateAsync(update, cancellationToken);

    public void ReportInstallFailure(string message)
    {
        StatusMessage = $"安装未启动：{message}";
    }

    public void ReportInstallReady()
    {
        if (DownloadedUpdate is { } update)
        {
            StatusMessage = $"版本 {update.Release.Version.ToString(3)} 已下载，可以安装。";
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
        _downloadCancellation?.Cancel();
        _ownedHttpClient?.Dispose();
        _lifetime.Dispose();
        OnPropertyChanged(nameof(UpdateEntryLabel));
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
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _automaticCheckAndDownload = true;
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
                new UpdatePreferences(AutomaticCheckAndDownload), SettingsJsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"更新偏好无法保存：{exception.Message}";
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

    private sealed record UpdatePreferences(bool AutomaticCheckAndDownload);
}
