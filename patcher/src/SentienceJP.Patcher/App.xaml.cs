using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SentienceJP.Core;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Platform;
using SentienceJP.Core.Steam;
using SentienceJP.Patcher.Services;
using SentienceJP.Patcher.ViewModels;

namespace SentienceJP.Patcher;

/// <summary>
/// アプリケーションの起動処理。
/// 0. 自己検査モード（--verify-payload）なら、画面を出さずに埋め込みペイロードを検証して終了コードで結果を返す
/// 1. 多重起動を防ぐ（既に起動していれば知らせて終了する）
/// 2. ペイロード（訳文データ等）を読み込んで検証する
/// 3. Steam からゲームのインストール先を検出する（見つからなければメッセージを出して終了）
/// 4. メイン画面を表示する
/// </summary>
public partial class App : Application
{
    /// <summary>未インストール時に表示するメッセージ。</summary>
    private const string NotInstalledMessage =
        "Sentience: The Android's Tale がインストールされていません。Steam でインストールしてから再度起動してください。";

    /// <summary>メッセージボックスのタイトル。</summary>
    private const string DialogTitle = "Sentience 日本語化パッチ";

    /// <summary>多重起動防止の Mutex の名前（別のサインインセッションで起動したものとも排他するため Global にする）。</summary>
    private const string SingleInstanceMutexName = @"Global\SentienceJP.Patcher.SingleInstance";

    /// <summary>自己検査で想定外のエラーが起きた（または結果を書き出せなかった）場合の終了コード。</summary>
    private const int SelfCheckUnexpectedErrorExitCode = 2;

    /// <summary>管理者としての再起動時に、前のインスタンスの終了を待つ時間。</summary>
    private static readonly TimeSpan ElevatedRestartWait = TimeSpan.FromSeconds(10);

    private readonly FileLogger _logger = FileLogger.CreateDefault();
    private SingleInstanceLock? _instanceLock;
    private MainViewModel? _viewModel;
    private bool _isHandlingFatalError;

    /// <summary>Windows のサインアウト・シャットダウンによる終了が始まっているか（画面を閉じる際にメッセージを出さないために使う）。</summary>
    public bool IsSessionEnding { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var commandLine = PatcherCommandLine.Parse(e.Args);

        // 自己検査モード（ビルドスクリプト用）: 画面・ログファイル・多重起動の確認を一切行わず、結果を終了コードで返す
        if (commandLine.VerifyPayload)
        {
            Shutdown(RunPayloadSelfCheck(commandLine.ReportPath));
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SessionEnding += OnSessionEnding;
        LogStartupEnvironment();
        try
        {
            StartupCore(commandLine);
        }
        catch (Exception ex)
        {
            // 起動処理中の想定外の例外は Dispatcher の例外ハンドラに届かないため、ここで記録・通知して終了する
            _logger.Error("起動処理で想定外のエラーが発生しました。", ex);
            MessageBox.Show(
                $"起動処理でエラーが発生したため終了します。{Environment.NewLine}{ErrorDescriber.Describe(ex)}{Environment.NewLine}{Environment.NewLine}{_logger.DescribeLocation()}",
                DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        // 多重起動防止のロックを解放する（Mutex は取得した UI スレッドで解放する必要がある）
        _instanceLock?.Dispose();
        _instanceLock = null;
        base.OnExit(e);
    }

    /// <summary>
    /// 埋め込みペイロードの自己検査を行い、終了コードを返す。結果ファイルの指定があれば、結果の説明を UTF-8 で書き出す。
    /// 画面を出すとビルドが止まってしまうため、想定外の例外も結果として書き出し、終了コードで知らせる。
    /// </summary>
    /// <param name="reportPath">結果を書き出すファイル（null なら書き出さない）。</param>
    private static int RunPayloadSelfCheck(string? reportPath)
    {
        PayloadSelfCheckResult result;
        try
        {
            result = PayloadSelfCheck.Run(PayloadSourceFactory.CreateEmbedded);
        }
        catch (Exception ex)
        {
            result = new PayloadSelfCheckResult(SelfCheckUnexpectedErrorExitCode, $"NG: 自己検査中に想定外のエラーが発生しました: {ex}");
        }

        if (reportPath is null)
        {
            return result.ExitCode;
        }

        try
        {
            File.WriteAllText(reportPath, result.Report + Environment.NewLine, new UTF8Encoding(false));
            return result.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 結果を報告できないため、検査自体が合格でも失敗の終了コードにする（呼び出し側は結果ファイルが無いことで気付く）
            return result.ExitCode == PayloadSelfCheck.Success ? SelfCheckUnexpectedErrorExitCode : result.ExitCode;
        }
    }

    /// <summary>起動処理の本体（多重起動の確認 → ペイロード読込 → ゲーム検出 → 画面表示）。</summary>
    private void StartupCore(PatcherCommandLine commandLine)
    {
        // 1. 多重起動の防止（2 つの画面から同時に操作すると、互いの変更やバックアップを壊しうるため）
        //    管理者としての再起動では、前のインスタンスが終了してロックを解放するまで少し待つ
        _instanceLock = SingleInstanceLock.TryAcquire(SingleInstanceMutexName, commandLine.ElevatedRestart ? ElevatedRestartWait : TimeSpan.Zero);
        if (_instanceLock is null)
        {
            _logger.Warn("既に起動しているため終了します。");
            MessageBox.Show("日本語化パッチは既に起動しています。開いている画面を使ってください。", DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // 2. ペイロードの読み込み（壊れていれば適用できないので終了する）
        PayloadBundle payload;
        try
        {
            var source = PayloadSourceFactory.Create();
            payload = PayloadBundle.Load(source);
            _logger.Info($"ペイロードを読み込みました（{payload.SourceDescription}、v{payload.Manifest.PatchVersion}、{payload.Manifest.Files.Count} ファイル）。");
        }
        catch (PatchException ex)
        {
            _logger.Error("ペイロードの読み込みに失敗しました。", ex);
            MessageBox.Show(
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}{_logger.DescribeLocation()}",
                DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 3. ゲームの検出（見つからなければその旨を表示して終了する）
        var candidates = new WindowsSteamRegistry().GetSteamRootCandidates();
        var located = SteamLocator.Locate(candidates.Roots, GameConstants.SteamAppId);
        foreach (var diagnostic in candidates.Diagnostics.Concat(located.Diagnostics))
        {
            _logger.Info($"[検出] {diagnostic}");
        }

        if (located.Installation is null)
        {
            _logger.Warn("ゲームが見つからないため終了します。");
            MessageBox.Show(
                $"{NotInstalledMessage}{Environment.NewLine}{Environment.NewLine}インストール済みの場合は、探索の記録を確認してください。{Environment.NewLine}{_logger.DescribeLocation()}",
                DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _logger.Info($"ゲームを検出しました: {located.Installation.GameDirectory}（buildid={located.Installation.Manifest.BuildId}）");

        // 4. メイン画面の表示
        var viewModel = new MainViewModel(
            located.Installation,
            payload,
            _logger,
            new MessageBoxDialogService(DialogTitle),
            new GameProcessChecker());
        _viewModel = viewModel;
        var window = new MainWindow(viewModel);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        viewModel.ReportLogFailureIfAny();
        viewModel.Refresh();
    }

    /// <summary>
    /// 起動時の環境（ツールのバージョン・exe の場所・OS・.NET・管理者として実行しているか）をログに残す（問い合わせ時の調査用）。
    /// </summary>
    private void LogStartupEnvironment()
    {
        var assembly = typeof(App).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "不明";
        _logger.Info(
            $"パッチツールを起動しました（バージョン: {version}、exe: {Environment.ProcessPath ?? "不明"}、"
            + $"OS: {RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）、.NET: {RuntimeInformation.FrameworkDescription}、"
            + $"管理者として実行: {(Environment.IsPrivilegedProcess ? "はい" : "いいえ")}）。");
    }

    /// <summary>
    /// Windows のサインアウト・シャットダウンの開始。操作の最中（確認ダイアログの表示中を含む。AA-07）は、
    /// ファイル操作が途中で止まらないよう保留する。
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        var reason = e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "シャットダウン" : "サインアウト";
        if (_viewModel is { IsOperationInProgress: true })
        {
            e.Cancel = true;
            _logger.Warn($"処理中のため、Windows の{reason}を保留しました。");
            return;
        }

        IsSessionEnding = true;
        _logger.Info($"Windows の{reason}のため終了します。");
    }

    /// <summary>
    /// 想定外の例外を記録して利用者に知らせ、安全のためアプリを終了する（握りつぶさない）。
    /// 適用・復元の実行中であれば、ファイル操作を途中で止めないよう、その完了を待ってから終了する。
    /// 例外を受けた時点で画面の操作を止め（新しい操作を始めさせない）、メッセージボックスはメイン画面を所有者にして表示し、
    /// 終了の直前にも、もう一度実行中の操作が無いことを待つ（AA-16）。
    /// </summary>
    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 既定の処理（即時終了）に任せると実行中のファイル操作が途中で止まるため、ここで処理済みにして終了を遅らせる
        e.Handled = true;
        _logger.Error("想定外のエラーが発生しました。", e.Exception);
        if (_isHandlingFatalError)
        {
            // 終了の手続きは 1 回だけ行う（2 つ目以降のエラーは記録のみ）
            return;
        }

        _isHandlingFatalError = true;

        // 終了までの間に新しい操作を始められないよう、メイン画面の操作をすぐに止める（AA-16）
        var mainWindow = MainWindow;
        if (mainWindow is not null)
        {
            mainWindow.IsEnabled = false;
        }

        try
        {
            await WaitForRunningOperationAsync();

            if (!IsSessionEnding)
            {
                // メイン画面を所有者にして表示する（所有者が無いと、利用者が別のアプリに切り替えていた場合にメイン画面が無効化されない。AA-16）
                var message = $"想定外のエラーが発生したため終了します。{Environment.NewLine}{ErrorDescriber.Describe(e.Exception)}{Environment.NewLine}{Environment.NewLine}{_logger.DescribeLocation()}";
                if (mainWindow is { IsVisible: true })
                {
                    MessageBox.Show(mainWindow, message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show(message, DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            // メッセージの表示中に操作が始まっていた場合に備え、終了の直前にもう一度、実行中の操作が終わるのを待つ（AA-16）
            await WaitForRunningOperationAsync();
        }
        finally
        {
            Shutdown(1);
        }
    }

    /// <summary>
    /// 操作の最中（確認ダイアログの表示中から結果の通知まで）であれば、その完了を待つ（想定外のエラーで終了する前に、ファイル操作を途中で止めないため）。
    /// </summary>
    private async Task WaitForRunningOperationAsync()
    {
        if (_viewModel is { IsOperationInProgress: true } viewModel)
        {
            _logger.Info("実行中の処理が終わるのを待ってから終了します。");
            await viewModel.WaitForIdleAsync();
        }
    }

    /// <summary>UI スレッド以外で起きた未処理例外（プロセスは終了する）を、原因調査のためにログへ残す。</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = $"想定外のエラーが発生しました（プロセスの終了: {(e.IsTerminating ? "あり" : "なし")}）。";
        if (e.ExceptionObject is Exception exception)
        {
            _logger.Error(message, exception);
        }
        else
        {
            _logger.Error($"{message}{Environment.NewLine}{e.ExceptionObject}");
        }
    }

    /// <summary>確認されないまま破棄されたバックグラウンド処理の例外を、ログへ残す。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        _logger.Error("バックグラウンド処理で、確認されなかったエラーが発生しました。", e.Exception);
}
