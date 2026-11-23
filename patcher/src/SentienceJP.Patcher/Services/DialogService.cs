using System.Windows;

namespace SentienceJP.Patcher.Services;

/// <summary>
/// 利用者への通知・確認の抽象。ViewModel を MessageBox から切り離すために使う。
/// </summary>
public interface IDialogService
{
    /// <summary>情報を表示する。</summary>
    /// <param name="message">内容。</param>
    void ShowInfo(string message);

    /// <summary>警告を表示する。</summary>
    /// <param name="message">内容。</param>
    void ShowWarning(string message);

    /// <summary>エラーを表示する。</summary>
    /// <param name="message">内容。</param>
    void ShowError(string message);

    /// <summary>はい／いいえで確認する。</summary>
    /// <param name="message">内容。</param>
    /// <param name="defaultToNo">
    /// 既定のボタン（Enter で選ばれる方）を「いいえ」にするか。
    /// 続行すると危険寄りの確認（ゲームが起動中かもしれない・未確認のバージョン等）では true にする。
    /// </param>
    /// <returns>「はい」が選ばれたら true。</returns>
    bool Confirm(string message, bool defaultToNo = false);
}

/// <summary>MessageBox による実装。</summary>
public sealed class MessageBoxDialogService : IDialogService
{
    private readonly string _title;

    /// <summary>ダイアログのタイトルを指定して作る。</summary>
    /// <param name="title">タイトル。</param>
    public MessageBoxDialogService(string title)
    {
        _title = title;
    }

    /// <inheritdoc />
    public void ShowInfo(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);

    /// <inheritdoc />
    public void ShowWarning(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK);

    /// <inheritdoc />
    public void ShowError(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);

    /// <inheritdoc />
    public bool Confirm(string message, bool defaultToNo = false) =>
        Show(
            message,
            MessageBoxButton.YesNo,
            defaultToNo ? MessageBoxImage.Warning : MessageBoxImage.Question,
            defaultToNo ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes;

    /// <summary>メイン画面を親にしてメッセージボックスを表示する。</summary>
    private MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var owner = Application.Current?.MainWindow;
        return owner is { IsVisible: true }
            ? MessageBox.Show(owner, message, _title, buttons, image, defaultResult)
            : MessageBox.Show(message, _title, buttons, image, defaultResult);
    }
}
