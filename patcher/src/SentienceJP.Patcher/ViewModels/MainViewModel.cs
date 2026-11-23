using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using SentienceJP.Core;
using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Platform;
using SentienceJP.Core.Plugins;
using SentienceJP.Core.Steam;
using SentienceJP.Patcher.Services;

namespace SentienceJP.Patcher.ViewModels;

/// <summary>
/// メイン画面の ViewModel。
/// 状態の判定・画面の構成・ファイル操作は Core の純粋関数・手続きに任せ、ここでは
/// 「事前確認 → 状態の取り直し → 実行（バックグラウンド）→ 状態の再表示 → 結果の通知」の流れだけを扱う。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>ERROR_CANCELLED（UAC のダイアログで「いいえ」が選ばれた）。</summary>
    private const int ErrorCancelled = 1223;

    private readonly GameInstallation _installation;
    private readonly PayloadBundle _payload;
    private readonly FileLogger _logger;
    private readonly IDialogService _dialogs;
    private readonly IGameProcessChecker _processChecker;
    private readonly StringBuilder _log = new();

    /// <summary>最後に判定した状態（まだ確認していない、または確認に失敗した場合は null）。</summary>
    private PatchStatus? _status;

    /// <summary>状態の確認に失敗した場合のエラー内容（確認前・成功時は null）。</summary>
    private string? _checkError;

    private bool _isBusy;

    /// <summary>操作（確認ダイアログの表示から結果の通知まで）の最中か（AA-07）。</summary>
    private bool _isOperationInProgress;

    /// <summary>現在の画面構成（<see cref="PatchScreenPlanner"/> が決める）。</summary>
    private PatchScreenPlan _screen;

    /// <summary>
    /// ログファイルに書き込めなかったことを、画面のログ欄で知らせ済みか。
    /// 書き込めるようになったら戻し、次に失敗したときにもう一度知らせる（AA-09）。
    /// </summary>
    private bool _logFailureShown;

    /// <summary>最後に開始した操作（完了を待てるようにするため保持する）。</summary>
    private Task _currentOperation = Task.CompletedTask;

    /// <summary>依存するサービスを受け取って作る。</summary>
    /// <param name="installation">検出したゲーム。</param>
    /// <param name="payload">検証済みのペイロード。</param>
    /// <param name="logger">ファイルロガー。</param>
    /// <param name="dialogs">ダイアログ表示。</param>
    /// <param name="processChecker">ゲームの起動状況の確認。</param>
    public MainViewModel(
        GameInstallation installation,
        PayloadBundle payload,
        FileLogger logger,
        IDialogService dialogs,
        IGameProcessChecker processChecker)
    {
        _installation = installation;
        _payload = payload;
        _logger = logger;
        _dialogs = dialogs;
        _processChecker = processChecker;
        _screen = PatchScreenPlanner.Plan(new PatchScreenInput(null, null, false));

        // どちらのコマンドも、別の操作の最中（確認ダイアログの表示中を含む）は実行できない（AA-07）
        MainCommand = new AsyncRelayCommand(ExecuteMainAsync, () => !_isOperationInProgress && _screen.MainButton.Enabled);
        RestoreCommand = new AsyncRelayCommand(() => StartOperation(PatchAction.Restore), () => !_isOperationInProgress && _screen.SecondaryRestoreEnabled);
    }

    /// <summary>ウィンドウのタイトル。</summary>
    public string WindowTitle => $"Sentience 日本語化パッチ v{_payload.Manifest.PatchVersion}";

    /// <summary>ゲームの場所。</summary>
    public string GameDirectory => _installation.GameDirectory;

    /// <summary>ビルドID の表示（未確認のビルドや更新中の可能性があれば注記する）。</summary>
    public string BuildIdText
    {
        get
        {
            var manifest = _installation.Manifest;
            var buildId = manifest.BuildId ?? "不明";
            var notes = new List<string>();
            if (_payload.Manifest.KnownBuildIds.Count > 0 && !_payload.Manifest.KnownBuildIds.Contains(buildId, StringComparer.Ordinal))
            {
                notes.Add("動作未確認のビルドです");
            }

            if (!manifest.IsFullyInstalled)
            {
                notes.Add("Steam で更新中・更新待ちの可能性があります");
            }

            return notes.Count == 0 ? buildId : $"{buildId}（{string.Join("、", notes)}）";
        }
    }

    /// <summary>状態の表示。</summary>
    public string StatusText => _screen.StatusText;

    /// <summary>状態の詳細（判定の根拠やエラー内容）。</summary>
    public string StatusDetail => _screen.StatusDetail;

    /// <summary>メインボタンの表示文字列。</summary>
    public string MainButtonText => _screen.MainButton.Label;

    /// <summary>補助の「元に戻す」ボタンを表示するか。</summary>
    public bool ShowSecondaryRestore => _screen.ShowSecondaryRestore;

    /// <summary>処理中か（ファイル操作の実行中。ボタンを無効化し、進行表示を出す）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateScreen();
            }
        }
    }

    /// <summary>
    /// 操作の最中か（確認ダイアログの表示中から、実行・結果の通知が終わるまで。AA-07）。
    /// 確認ダイアログの間に別の操作が始まって 2 つの操作が同時に走らないよう、両方のコマンドの実行可否に使う。
    /// 画面を閉じる・Windows の終了・想定外のエラー時の終了を待つかの判断にも使う。
    /// 進行表示（<see cref="IsBusy"/>）とは分け、確認ダイアログの間は進行表示を出さない。
    /// </summary>
    public bool IsOperationInProgress
    {
        get => _isOperationInProgress;
        private set
        {
            if (SetProperty(ref _isOperationInProgress, value))
            {
                MainCommand.RaiseCanExecuteChanged();
                RestoreCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>ログ表示欄の内容。</summary>
    public string LogText => _log.ToString();

    /// <summary>フッターの注記。</summary>
    public string FooterText => "非公式の日本語化パッチです。開発元とは無関係です。";

    /// <summary>メインボタンのコマンド（状態に応じた操作、または状態の再確認）。</summary>
    public AsyncRelayCommand MainCommand { get; }

    /// <summary>補助の「元に戻す」ボタンのコマンド。</summary>
    public AsyncRelayCommand RestoreCommand { get; }

    /// <summary>
    /// ゲームフォルダを読み取り直して、状態とボタン構成を更新する。
    /// 確認に失敗した場合は、主ボタンを「再確認する」にしてダイアログで知らせる。
    /// </summary>
    public void Refresh() => TryRefresh(logStatus: true);

    /// <summary>
    /// 実行中の操作があれば、その完了（成否を問わない）を待つ。
    /// 想定外のエラーで終了する前に、ファイル操作を途中で止めないために使う。
    /// </summary>
    public Task WaitForIdleAsync() =>
        _currentOperation.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// ログファイルへの書き込みに失敗していれば、画面のログ欄で知らせる（起動直後の確認用。失敗が続く間は一度だけ表示する）。
    /// </summary>
    public void ReportLogFailureIfAny()
    {
        if (!_logFailureShown && _logger.LastError is not null)
        {
            AppendScreenOnly(string.Empty);
            OnPropertyChanged(nameof(LogText));
        }
    }

    /// <summary>
    /// 状態を判定し直して画面に反映する。
    /// </summary>
    /// <param name="logStatus">判定した状態をログ欄に書くか。</param>
    /// <returns>判定できた場合は true。</returns>
    private bool TryRefresh(bool logStatus)
    {
        try
        {
            var status = PatchStatusDetector.Detect(GameSnapshotReader.Read(_installation.GameDirectory, _payload.Manifest), _payload.Manifest);
            _status = status;
            _checkError = null;
            UpdateScreen();
            if (logStatus)
            {
                AppendLog($"状態: {_screen.StatusText}");

                // 通常の状態（未適用・適用済み）以外では、判定の根拠もログに残す
                if (status.Kind is not (PatchStatusKind.NotApplied or PatchStatusKind.Applied))
                {
                    foreach (var reason in status.Reasons)
                    {
                        AppendLog($"  - {reason}");
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PatchException)
        {
            // よくある例外は日本語の説明にし、英語の原文は括弧書きで残す（AA-13。例外の詳細はログにも残す）
            var described = ErrorDescriber.Describe(ex);
            _status = null;
            _checkError = described;
            UpdateScreen();
            _logger.Error("状態の確認に失敗しました。", ex);
            AppendLog($"状態を確認できませんでした: {described}");
            _dialogs.ShowError(
                $"ゲームフォルダの状態を確認できませんでした。{Environment.NewLine}{described}{Environment.NewLine}{Environment.NewLine}"
                + $"原因を取り除いてから「{PatchScreenPlanner.RecheckLabel}」を押してください。");
            return false;
        }
    }

    /// <summary>画面構成を決め直し、表示とボタンの状態へ反映する。</summary>
    private void UpdateScreen()
    {
        _screen = PatchScreenPlanner.Plan(new PatchScreenInput(_status, _checkError, _isBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(MainButtonText));
        OnPropertyChanged(nameof(ShowSecondaryRestore));
        MainCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
    }

    /// <summary>主ボタンの処理: 状態に応じた操作、または（状態が分からない・行える操作が無い場合は）状態の再確認。</summary>
    private Task ExecuteMainAsync()
    {
        if (_screen.MainButton.Action is { } action)
        {
            return StartOperation(action);
        }

        AppendLog("状態を確認し直します…");
        Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 操作を開始する（AA-07）。確認ダイアログを出す前に「操作の最中」の印を立て、既に別の操作の最中なら何もしないで戻る
    /// （確認ダイアログの表示中に、もう一方のボタンが UI オートメーション等で実行されても、2 つの操作を同時に走らせない）。
    /// 開始した操作は、完了を待てる（<see cref="WaitForIdleAsync"/>）よう記録する。
    /// </summary>
    private Task StartOperation(PatchAction action)
    {
        if (_isOperationInProgress)
        {
            _logger.Info($"別の操作の最中のため、{DescribeAction(action)}は開始しませんでした。");
            return Task.CompletedTask;
        }

        IsOperationInProgress = true;
        var operation = RunOperationAsync(action);
        _currentOperation = operation;
        return operation;
    }

    /// <summary>操作を実行し、終わったら（失敗・中止でも）「操作の最中」の印を下ろす。</summary>
    private async Task RunOperationAsync(PatchAction action)
    {
        try
        {
            await ExecuteAsync(action);
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    /// <summary>
    /// 操作を実行する: ゲームフォルダの存在確認 → 事前確認 → 状態の取り直し → バックグラウンドで実行 → 状態を更新 → 結果を通知。
    /// </summary>
    private async Task ExecuteAsync(PatchAction action)
    {
        // 画面を開いたままゲームフォルダが無くなっていれば（アンインストール・移動・外付けドライブの取り外し）、
        // 確認ダイアログを出す前に止め、表示を更新して起動し直すよう案内する（AA-08。何も変更しない）
        if (GameValidator.DescribeMissingGame(_installation.GameDirectory) is { } missingGame)
        {
            AppendLog($"{missingGame}（{DescribeAction(action)}を中止しました）");
            TryRefresh(logStatus: true);
            _dialogs.ShowWarning(PatchMessages.GameNotFoundGuidance);
            return;
        }

        if (!ConfirmPreconditions(action))
        {
            return;
        }

        // 確認ダイアログを出している間などに状態が変わっていないか、実行の直前に取り直して確かめる（古い画面のまま実行しないため）
        if (!TryRefresh(logStatus: false))
        {
            return;
        }

        if (_status is not { } current || !PatchScreenPlanner.IsActionAvailable(current, action))
        {
            AppendLog($"状態が変わったため、{DescribeAction(action)}を中止しました（現在の状態: {_screen.StatusText}）。");
            _dialogs.ShowWarning(
                $"ゲームフォルダの状態が変わったため、{DescribeAction(action)}を中止しました。{Environment.NewLine}"
                + "画面の表示を更新したので、内容を確認してからもう一度操作してください。");
            return;
        }

        IsBusy = true;
        AppendLog($"--- {DescribeAction(action)}を開始します ---");
        _logger.Info($"操作を開始します: {action}（状態: {current.Kind}）");
        PatchOperationResult result;
        try
        {
            // 進行状況は UI スレッドへ転送して表示とファイルの両方に残す
            var progress = new Progress<string>(AppendLog);
            IProgress<string> reporter = progress;
            var installation = _installation;
            var payload = _payload;
            result = await Task.Run(() => PatchOperations.Execute(
                action,
                installation.GameDirectory,
                installation.Manifest.BuildId,
                payload,
                reporter.Report));
        }
        finally
        {
            IsBusy = false;
        }

        // 結果を伝える前に状態を取り直す（失敗後の状態に応じた補足を添えるため）
        var refreshed = TryRefresh(logStatus: true);
        ReportResult(action, result, refreshed ? _status : null);
    }

    /// <summary>結果をログとダイアログで通知する。</summary>
    /// <param name="action">実行した操作。</param>
    /// <param name="result">結果。</param>
    /// <param name="statusAfter">実行後に取り直した状態（取得できなかった場合は null）。</param>
    private void ReportResult(PatchAction action, PatchOperationResult result, PatchStatus? statusAfter)
    {
        if (result.Success)
        {
            _logger.Info($"操作が完了しました: {action} — {result.Message}");
            _dialogs.ShowInfo(result.Message);
            return;
        }

        _logger.Error($"操作に失敗しました: {action} — {result.Message}", result.Error);
        foreach (var cleanupError in result.CleanupErrors)
        {
            _logger.Error($"後始末の失敗: {cleanupError}");
        }

        var newLine = Environment.NewLine;
        var message = new StringBuilder(result.Message);
        var followUp = PatchScreenPlanner.DescribeFailureFollowUp(action, statusAfter);
        if (followUp is not null)
        {
            AppendLog(followUp);
            message.Append(newLine).Append(newLine).Append(followUp);
        }

        if (result.CleanupErrors.Count > 0)
        {
            message.Append(newLine).Append(newLine).Append("取り消せなかった変更:").Append(newLine)
                .Append(string.Join(newLine, result.CleanupErrors));
        }

        message.Append(newLine).Append(newLine).Append(_logger.DescribeLocation(logViewAvailable: true));
        _dialogs.ShowError(message.ToString());
    }

    /// <summary>
    /// 実行前の確認。ゲームの起動状況、Steam の更新状況、書き込み権限、（適用時は）ゲームのバージョンを確認する。
    /// 続行すると危険寄りの確認では、既定のボタンを「いいえ」にする。
    /// </summary>
    /// <returns>実行してよければ true。</returns>
    private bool ConfirmPreconditions(PatchAction action)
    {
        // 1. ゲームが起動中なら実行しない
        var processState = _processChecker.Check(_installation.GameDirectory);
        switch (processState)
        {
            case GameProcessState.Running:
                _dialogs.ShowWarning("ゲームが起動中です。ゲームを終了してから、もう一度実行してください。");
                return false;
            case GameProcessState.Unknown:
                if (!_dialogs.Confirm(
                        "nw.exe が起動していますが、ゲームのものか確認できませんでした。\nゲームを終了していることを確認のうえ、続行しますか？",
                        defaultToNo: true))
                {
                    return false;
                }

                break;
            case GameProcessState.NotRunning:
                break;
            default:
                throw new InvalidEnumArgumentException(nameof(processState), (int)processState, typeof(GameProcessState));
        }

        // 2. Steam で更新中・更新待ちの可能性があれば確認する（Steam の更新とファイルの書き換えが重なるのを避けるため）
        var appManifest = ReadCurrentAppManifest();
        if (!appManifest.IsFullyInstalled
            && !_dialogs.Confirm(
                $"Steam でゲームの更新中、または更新待ちの可能性があります（appmanifest の StateFlags={appManifest.StateFlags?.ToString(CultureInfo.InvariantCulture) ?? "不明"}）。"
                + $"{Environment.NewLine}更新が終わってから実行することをおすすめします。続行しますか？",
                defaultToNo: true))
        {
            return false;
        }

        // 3. 書き込めなければ、原因に応じて案内する（権限不足で未昇格なら、管理者としての再起動を提案する）
        var problems = WriteAccessChecker.FindProblems(_installation.GameDirectory, GameConstants.DirectoriesRequiringWriteAccess);
        if (problems.Count > 0)
        {
            var advice = WriteAccessChecker.Advise(problems, Environment.IsPrivilegedProcess);
            _logger.Warn($"ゲームフォルダに書き込めません（管理者として実行: {Environment.IsPrivilegedProcess}）:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
            if (advice.OfferRestartAsAdministrator)
            {
                if (_dialogs.Confirm(advice.Message))
                {
                    RestartAsAdministrator();
                }
            }
            else
            {
                _dialogs.ShowWarning(advice.Message);
            }

            return false;
        }

        // 4. 適用を伴う操作では、動作確認済みの plugins.js かを確認する
        if (action is PatchAction.Apply or PatchAction.Reapply or PatchAction.Update)
        {
            var warning = DescribeUnknownGameVersion();
            if (warning is not null && !_dialogs.Confirm(warning, defaultToNo: true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// appmanifest を読み直す（起動後に Steam が更新を始めた・終えた場合も正しく確認するため）。
    /// 読めなかった場合は起動時に読んだ内容を使い、その旨をログに残す。
    /// </summary>
    private AppManifest ReadCurrentAppManifest()
    {
        try
        {
            return AppManifest.Parse(File.ReadAllText(_installation.ManifestPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyValuesParseException)
        {
            _logger.Warn($"appmanifest を読み直せなかったため、起動時の内容で確認します: {_installation.ManifestPath}（{ex.Message}）");
            return _installation.Manifest;
        }
    }

    /// <summary>
    /// plugins.js（パッチのエントリを除いた内容）が動作確認済みのものと一致しなければ、警告文を返す。
    /// 一致する、または確認対象が設定されていなければ null。
    /// </summary>
    private string? DescribeUnknownGameVersion()
    {
        var known = _payload.Manifest.KnownPluginsJsSha256;
        if (known.Count == 0)
        {
            return null;
        }

        try
        {
            var path = GamePaths.ToFullPath(_installation.GameDirectory, GameConstants.PluginsJsRelativePath);
            var (text, hasBom) = Utf8Text.Decode(File.ReadAllBytes(path), "plugins.js");
            var (baseText, _) = PluginsJsEditor.RemoveEntry(text, GameConstants.PluginName);
            var sha = Hashing.Sha256Hex(Utf8Text.Encode(baseText, hasBom));
            if (known.Contains(sha, StringComparer.Ordinal))
            {
                return null;
            }

            _logger.Warn($"動作未確認の plugins.js です（sha256={sha}）。");
            return "このゲームのバージョンは、日本語化パッチの動作確認に使ったものと異なるようです（plugins.js の内容が一致しません）。\n表示が崩れたり、一部が英語のままになる可能性があります。続行しますか？";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PatchException or PluginsJsFormatException)
        {
            _logger.Error("plugins.js のバージョン確認に失敗しました。", ex);
            return $"plugins.js の内容を確認できませんでした（{ErrorDescriber.Describe(ex)}）。続行しますか？";
        }
    }

    /// <summary>管理者として自分自身を再起動し、成功したら現在のプロセスを終了する。</summary>
    private void RestartAsAdministrator()
    {
        var path = Environment.ProcessPath;
        if (path is null)
        {
            _dialogs.ShowError("実行ファイルの場所が分からないため、管理者として再起動できません。exe を右クリックして「管理者として実行」を選んでください。");
            return;
        }

        try
        {
            // 新しいインスタンスには、このインスタンスの終了（多重起動防止のロックの解放）を待つよう伝える
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = PatcherCommandLine.ElevatedRestartSwitch,
            });
            _logger.Info("管理者として再起動します。");

            // ここは操作の事前確認（ファイル操作の前）の途中なので、「操作の最中」の印を下ろしてから終了する。
            // 印が立ったままだと、ウィンドウを閉じるときの確認（MainWindow.OnClosing）が「処理中のため閉じられません」を表示し、
            // それを閉じるまでこのインスタンスが多重起動防止のロックを持ち続けるため、管理者として起動した新しいインスタンスが
            // 待ち時間（10 秒）を使い切って「既に起動しています」で終わってしまう（AA-07 の印を確認前から立てるようにしたことへの対処）。
            IsOperationInProgress = false;
            Application.Current.Shutdown(0);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logger.Info("管理者としての再起動はキャンセルされました。");
            _dialogs.ShowInfo("管理者としての再起動はキャンセルされました。");
        }
        catch (Win32Exception ex)
        {
            _logger.Error("管理者として再起動できませんでした。", ex);
            _dialogs.ShowError($"管理者として再起動できませんでした: {ErrorDescriber.Describe(ex)}");
        }
    }

    /// <summary>ログ表示欄とログファイルに 1 行追加する。</summary>
    private void AppendLog(string message)
    {
        _logger.Info(message);
        AppendScreenOnly(message);
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>
    /// ログ表示欄だけに 1 行追加する（空文字なら行は追加しない）。
    /// ログファイルへの書き込みに失敗していれば、一部の記録がファイルに残っていないことを画面に表示する（失敗が続く間は一度だけ）。
    /// 一時的な失敗の後に書き込めるようになれば、ロガーが失敗の記録を消すので、以降は通常どおりログの場所を案内する（AA-09）。
    /// その後にまた失敗したら、もう一度知らせる。
    /// </summary>
    private void AppendScreenOnly(string message)
    {
        var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (message.Length > 0)
        {
            _log.Append(time).Append("  ").AppendLine(message);
        }

        if (_logger.LastError is not { } error)
        {
            _logFailureShown = false;
            return;
        }

        if (!_logFailureShown)
        {
            _logFailureShown = true;
            _log.Append(time).Append("  ").AppendLine($"※ 一部の記録をログファイルに書き込めませんでした。{error}");
        }
    }

    /// <summary>操作の日本語名。</summary>
    private static string DescribeAction(PatchAction action) => action switch
    {
        PatchAction.Apply => "日本語化",
        PatchAction.Restore => "元に戻す処理",
        PatchAction.Reapply => "再適用",
        PatchAction.Update => "更新",
        _ => action.ToString(),
    };
}
