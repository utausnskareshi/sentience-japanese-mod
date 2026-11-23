using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SentienceJP.Patcher.ViewModels;

namespace SentienceJP.Patcher;

/// <summary>
/// メイン画面。表示とバインディングのみを担い、処理は <see cref="MainViewModel"/> に任せる。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>ViewModel を受け取って画面を作る。</summary>
    /// <param name="viewModel">メイン画面の ViewModel。</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// 操作の最中（確認ダイアログの表示中から結果の通知まで。AA-07）はウィンドウを閉じさせない
    /// （ファイル操作の途中でプロセスが終わり、中途半端な状態になるのを防ぐ）。
    /// Windows のサインアウト・シャットダウン中は、メッセージボックスで終了処理を止めないよう、表示せずに閉じるのを取りやめる
    /// （シャットダウン自体の保留は <see cref="App"/> の SessionEnding で行う）。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsOperationInProgress)
        {
            e.Cancel = true;
            if (Application.Current is not App { IsSessionEnding: true })
            {
                MessageBox.Show(this, "処理中のため閉じられません。完了するまでお待ちください。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        base.OnClosing(e);
    }

    /// <summary>ログが追加されたら末尾までスクロールする（表示上の処理のためコードビハインドで行う）。</summary>
    private void OnLogTextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
