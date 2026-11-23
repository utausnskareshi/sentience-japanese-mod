using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using SentienceJP.Core.Platform;
using SentienceJP.Patcher.ViewModels;

namespace SentienceJP.Patcher.Tests;

/// <summary>
/// メイン画面の読み上げ名と文字色のテスト（AA-10）。画面は表示せず、ウィンドウを作って要素を調べるだけにする。
/// メイン画面のボタンのスタイルはアプリのリソース（App.xaml）にあるため、アプリのオブジェクトを 1 つだけ作ってリソースを読み込む
/// （WPF ではアプリのオブジェクトはプロセスに 1 つしか作れないため、これを作るテストはこのクラスの 1 つだけにする）。
/// </summary>
public sealed class MainWindowAccessibilityTests
{
    [Fact]
    public void ControlsWithoutVisibleText_HaveAutomationNames_AndSecondaryTextUsesSystemGray()
    {
        using var setup = new FakeGameSetup();
        var names = new List<string>();
        var grayLabels = new List<string>();
        var nonSystemColoredLabels = new List<string>();

        var unhandled = DispatcherRunner.Run(() =>
        {
            var app = new App();
            app.InitializeComponent();

            // WPF は App を作った時点で、起動処理（App.OnStartup）の呼び出しを Dispatcher に予約し、Dispatcher が動き出すと実行する
            // （Run を呼ばなくても、このテストの Dispatcher で実行される）。このテストはリソース（App.xaml のスタイル）を読み込むためだけに
            // App を作るので、すぐに終了を要求して起動処理を行わせない（WPF は予約した起動処理の直前に終了の要求済みかを確かめ、
            // 要求済みなら OnStartup を呼ばない）。起動処理が動くと、実機の Steam とゲームフォルダを調べ、利用者のログ
            // （%LOCALAPPDATA%\SentienceJP\logs）に書き込み、多重起動防止のロックを取って画面を出してしまう。パッチツールを開いたまま
            // テストすると「既に起動しています」、Steam の無い環境（CI など）では「ゲームが見つかりません」のメッセージボックスで止まり、
            // タイムアウトで失敗していた（LT-03）。終了の要求は InitializeComponent の後に行う（App.xaml で ShutdownMode を設定しており、
            // 終了の要求後は設定できないため）。終了の処理（ウィンドウを閉じる・Exit イベント）は、この本体が終わった後に行われる。
            app.Shutdown();
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), new FileLogger(setup.LogDirectory), new FakeDialogs(), new FakeProcessChecker(GameProcessState.NotRunning));
            vm.Refresh();
            var window = new MainWindow(vm);
            var root = (DependencyObject)window.Content;

            // 読み上げソフトに伝わる名前（UI オートメーションの Name）。表示文字列を持たないコントロールにも名前があること
            foreach (var control in Descendants<Control>(root).Where(c => c is TextBox or ProgressBar))
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(control);
                names.Add($"{control.GetType().Name}:{peer.GetName()}");
            }

            // 補助的な文字（見出しのラベル・状態の詳細・注記）は、固定色ではなくシステムの GrayText の色
            foreach (var text in Descendants<TextBlock>(root).Where(t => t.Text is "ゲームの場所" or "ビルドID" or "状態" or "ログ"))
            {
                var color = (text.Foreground as SolidColorBrush)?.Color;
                (color == SystemColors.GrayTextColor ? grayLabels : nonSystemColoredLabels).Add(text.Text);
            }

            return Task.CompletedTask;
        });

        Assert.Empty(unhandled);
        Assert.Contains("TextBox:ゲームの場所", names);
        Assert.Contains("TextBox:ログ", names);
        Assert.Contains("ProgressBar:処理中", names);
        Assert.Empty(nonSystemColoredLabels);
        Assert.Equal(["ゲームの場所", "ビルドID", "状態", "ログ"], grayLabels);
    }

    /// <summary>論理ツリーの子孫から、指定した型の要素を列挙する（表示していないウィンドウでも辿れる）。</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
