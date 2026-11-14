using System.Windows;
using SkinManager.Market;

namespace SkinManager.Desktop;

internal sealed class MarketAccountSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly bool _ownsClient;
    private readonly bool _ownsAuthProvider;
    private bool _isBusy;
    private bool _disposed;

    internal MarketAccountSession(
        SkinMarketClient client,
        IMarketAccountAuthProvider? authProvider,
        bool ownsClient,
        bool ownsAuthProvider)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        AuthProvider = authProvider;
        _ownsClient = ownsClient;
        _ownsAuthProvider = ownsAuthProvider;
    }

    internal SkinMarketClient Client { get; }
    internal IMarketAccountAuthProvider? AuthProvider { get; }
    internal SkinMarketAccount? Account => Client.Account;
    internal bool IsBusy => _isBusy;

    internal event EventHandler? StateChanged;

    internal async Task<SkinMarketAccount?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (AuthProvider is null)
        {
            return Client.Account;
        }

        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            return await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    internal async Task<SkinMarketAccount?> SignInAsync(
        Window owner,
        Uri startUri,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(startUri);
        var provider = AuthProvider
            ?? throw new InvalidOperationException("当前窗口没有启用账号登录。");
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            await provider.InitializeAsync(cancellationToken);
            await provider.OpenAsync(owner, startUri, cancellationToken);
            return await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    internal async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            if (AuthProvider is not null)
            {
                await AuthProvider.ClearCookiesAsync(cancellationToken);
            }

            Client.ClearAccount();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    internal async Task<SkinMarketFeedbackResult> SubmitFeedbackAsync(
        string title,
        string message,
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        SetBusy(true);
        try
        {
            return await Client.SubmitFeedbackAsync(title, message, appVersion, cancellationToken);
        }
        catch (SkinMarketReauthenticationException)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
        }
    }

    internal void NotifyAccountChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _operationGate.WaitAsync();
        try
        {
            if (_ownsAuthProvider && AuthProvider is not null)
            {
                await AuthProvider.DisposeAsync();
            }

            if (_ownsClient)
            {
                Client.Dispose();
            }
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private async Task<SkinMarketAccount?> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var provider = AuthProvider
            ?? throw new InvalidOperationException("当前窗口没有启用账号登录。");
        await provider.InitializeAsync(cancellationToken);
        var cookieHeader = await provider.ReadCookieHeaderAsync(cancellationToken);
        var account = await Client.RefreshAccountAsync(cookieHeader, cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return account;
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MarketAccountSession));
        }
    }
}
