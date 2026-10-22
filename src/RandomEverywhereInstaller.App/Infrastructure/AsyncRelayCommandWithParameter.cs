using System.Windows.Input;

namespace RandomEverywhereInstaller.App.Infrastructure;

public sealed class AsyncRelayCommandWithParameter<T>(Func<T?, Task> execute, Func<T?, bool>? canExecute = null) : ICommand where T : class
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter as T) ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(parameter as T).ConfigureAwait(true); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
