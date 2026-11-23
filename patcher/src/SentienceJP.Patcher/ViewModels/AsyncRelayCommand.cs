using System.Windows.Input;

namespace SentienceJP.Patcher.ViewModels;

/// <summary>
/// 非同期処理を実行するコマンド。実行中は再入を防ぎ、実行可否の変化を通知する。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _isRunning;

    /// <summary>実行処理と実行可否の判定を指定して作る。</summary>
    /// <param name="execute">実行する非同期処理。</param>
    /// <param name="canExecute">実行可能かの判定。</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_isRunning && _canExecute();

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        // async void は ICommand の制約によるもの。例外は _execute 側で必ず処理し、
        // 万一ここまで届いた場合は Dispatcher の未処理例外ハンドラで記録・通知される。
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>実行可否が変わったことを通知する。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
