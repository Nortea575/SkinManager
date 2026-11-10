using System.Windows.Threading;
using SkinManager.Runtime;

namespace SkinManager.Desktop;

public partial class MainWindow
{
    private long _runtimeOperationGeneration;
    private long _activeRuntimeOperationGeneration;

    private long BeginRuntimeOperation(string? message = null, bool cancellable = false)
    {
        var generation = Interlocked.Increment(ref _runtimeOperationGeneration);
        Interlocked.Exchange(ref _activeRuntimeOperationGeneration, generation);
        SetRuntimeOperationInProgress(true, message, cancellable);
        return generation;
    }

    private void FinishRuntimeOperation(long generation)
    {
        if (Interlocked.Read(ref _activeRuntimeOperationGeneration) == generation)
        {
            Interlocked.Exchange(ref _activeRuntimeOperationGeneration, 0);
        }

        SetRuntimeOperationInProgress(false);
    }

    private void QueueRuntimeProgress(RuntimeStatus status)
    {
        var generation = Interlocked.Read(ref _activeRuntimeOperationGeneration);
        if (generation == 0 || _windowClosed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_windowClosed && _runtimeOperationInProgress &&
                Interlocked.Read(ref _activeRuntimeOperationGeneration) == generation)
            {
                DisplayRuntimeStatus(status);
            }
        }, DispatcherPriority.DataBind);
    }
}
