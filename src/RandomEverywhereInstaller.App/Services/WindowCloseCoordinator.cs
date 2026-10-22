using System.Windows.Threading;

namespace RandomEverywhereInstaller.App.Services;

// Defer cleanup beyond the Closing event, including synchronously completed disposal.
public sealed class WindowCloseCoordinator(
    Dispatcher dispatcher, Func<Task> dispose, Action close, Action<Exception> reportError)
{
    private bool _requested;
    public bool CanClose { get; private set; }

    public void Request()
    {
        dispatcher.VerifyAccess();
        if (_requested) return;
        _requested = true;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await dispose(); }
            catch (Exception error) { reportError(error); }
            finally
            {
                CanClose = true;
                close();
            }
        }));
    }
}
