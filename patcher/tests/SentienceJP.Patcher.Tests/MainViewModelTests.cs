using System.IO;
using SentienceJP.Core.Patching;
using SentienceJP.Core.Platform;
using SentienceJP.Patcher.ViewModels;

namespace SentienceJP.Patcher.Tests;

/// <summary>
/// メイン画面の ViewModel の配線のテスト（偽のダイアログ・偽のプロセス確認・テンポラリの偽ゲームフォルダで実行し、画面は表示しない）。
/// - AA-07: 確認ダイアログの表示中に、もう一方のコマンドで 2 つ目の操作が始まらない。確認中は進行表示を出さない。
/// - AA-08: 画面を開いたままゲームフォルダが無くなった場合、「見つからない」と案内し、操作ボタンは出さない（副作用なし）。
/// - AA-09: ログの一時的な書き込み失敗の後は、通常どおりログの場所を案内する。
/// </summary>
public sealed class MainViewModelTests
{
    [Fact]
    public void RestoreInvokedWhileMainConfirmIsOpen_DoesNotStartASecondOperation()
    {
        // AA-07: 主ボタン（更新）の確認ダイアログの表示中に、補助の「元に戻す」が（UI オートメーション等で）実行されても、2 つ目の操作は始まらない
        using var setup = new FakeGameSetup();
        Assert.True(PatchApplier.Apply(setup.GameDirectory, "2476493", FakeGameSetup.Payload("0.0.9"), _ => { }).Success);
        var dialogs = new FakeDialogs();
        var duringConfirm = new List<string>();
        string? finalStatus = null;
        var startedOperations = -1;

        var unhandled = DispatcherRunner.Run(async () =>
        {
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), new FileLogger(setup.LogDirectory), dialogs, new FakeProcessChecker(GameProcessState.Unknown));
            vm.Refresh();
            dialogs.OnConfirm = (count, _) =>
            {
                if (count == 1)
                {
                    duringConfirm.Add($"main={vm.MainCommand.CanExecute(null)} restore={vm.RestoreCommand.CanExecute(null)} inProgress={vm.IsOperationInProgress} busy={vm.IsBusy}");
                    vm.RestoreCommand.Execute(null);
                    duringConfirm.Add($"after nested restore: busy={vm.IsBusy}");
                }

                return true;
            };

            vm.MainCommand.Execute(null);
            await DispatcherRunner.WaitUntil(() => !vm.IsOperationInProgress);
            startedOperations = vm.LogText.Split('\n').Count(line => line.Contains("を開始します ---", StringComparison.Ordinal));
            finalStatus = vm.StatusText;
            Assert.True(vm.MainCommand.CanExecute(null), "操作が終われば、再び押せる");
        });

        Assert.Empty(unhandled);
        Assert.Equal("main=False restore=False inProgress=True busy=False", duringConfirm[0]);
        Assert.Equal("after nested restore: busy=False", duringConfirm[1]);
        Assert.Equal(1, startedOperations);
        Assert.Equal(1, dialogs.Calls.Count(call => call.StartsWith("Confirm#", StringComparison.Ordinal)));
        Assert.Single(dialogs.Calls, call => call.StartsWith("Info: ", StringComparison.Ordinal));
        Assert.DoesNotContain(dialogs.Calls, call => call.StartsWith("Error: ", StringComparison.Ordinal));
        Assert.Contains("適用済み", finalStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void MainInvokedWhileRestoreConfirmIsOpen_DoesNotStartASecondOperation()
    {
        // AA-07（逆向き）: 補助の「元に戻す」の確認ダイアログの表示中に、主ボタンが実行されても、2 つ目の操作は始まらない
        using var setup = new FakeGameSetup();
        Assert.True(PatchApplier.Apply(setup.GameDirectory, "2476493", FakeGameSetup.Payload("0.0.9"), _ => { }).Success);
        var dialogs = new FakeDialogs();
        var startedOperations = -1;

        var unhandled = DispatcherRunner.Run(async () =>
        {
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), new FileLogger(setup.LogDirectory), dialogs, new FakeProcessChecker(GameProcessState.Unknown));
            vm.Refresh();
            dialogs.OnConfirm = (count, _) =>
            {
                if (count == 1)
                {
                    vm.MainCommand.Execute(null);
                }

                return true;
            };

            vm.RestoreCommand.Execute(null);
            await DispatcherRunner.WaitUntil(() => !vm.IsOperationInProgress);
            startedOperations = vm.LogText.Split('\n').Count(line => line.Contains("を開始します ---", StringComparison.Ordinal));
        });

        Assert.Empty(unhandled);
        Assert.Equal(1, startedOperations);
        Assert.DoesNotContain(dialogs.Calls, call => call.StartsWith("Error: ", StringComparison.Ordinal));
        Assert.Equal(PatchStatusKind.NotApplied, PatchStatusDetector.Detect(GameSnapshotReader.Read(setup.GameDirectory, FakeGameSetup.Payload("0.1.0").Manifest), FakeGameSetup.Payload("0.1.0").Manifest).Kind);
    }

    [Fact]
    public void ConfirmDeclined_EndsTheOperation_WithoutProgressDisplay()
    {
        // AA-07: 確認で「いいえ」を選べば、操作の最中の印は下り、進行表示も一度も出ない
        using var setup = new FakeGameSetup();
        var dialogs = new FakeDialogs { OnConfirm = (_, _) => false };
        var busySeen = false;

        var unhandled = DispatcherRunner.Run(() =>
        {
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), new FileLogger(setup.LogDirectory), dialogs, new FakeProcessChecker(GameProcessState.Unknown));
            vm.PropertyChanged += (_, e) => busySeen |= e.PropertyName == nameof(MainViewModel.IsBusy) && vm.IsBusy;
            vm.Refresh();

            vm.MainCommand.Execute(null);

            Assert.False(vm.IsOperationInProgress);
            Assert.True(vm.MainCommand.CanExecute(null));
            return Task.CompletedTask;
        });

        Assert.Empty(unhandled);
        Assert.False(busySeen);
        Assert.False(Directory.Exists(Path.Combine(setup.GameDirectory, "sentience_jp_backup")));
    }

    [Fact]
    public void GameFolderRemovedWhileOpen_RefreshShowsNotFoundAndOffersOnlyRecheck()
    {
        // AA-08: 画面を開いたままゲームフォルダが無くなったら、「不整合」「元に戻す（クリーンアップ）」ではなく、見つからない旨と再確認だけを出す
        using var setup = new FakeGameSetup();
        var dialogs = new FakeDialogs();
        string? statusText = null;
        string? mainButton = null;
        var showSecondary = true;
        string? statusDetail = null;

        var unhandled = DispatcherRunner.Run(() =>
        {
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), new FileLogger(setup.LogDirectory), dialogs, new FakeProcessChecker(GameProcessState.NotRunning));
            vm.Refresh();
            Directory.Delete(setup.GameDirectory, recursive: true);

            vm.Refresh();
            statusText = vm.StatusText;
            mainButton = vm.MainButtonText;
            showSecondary = vm.ShowSecondaryRestore;
            statusDetail = vm.StatusDetail;

            // 主ボタン（再確認）を押しても、何も作らない
            vm.MainCommand.Execute(null);
            return Task.CompletedTask;
        });

        Assert.Empty(unhandled);
        Assert.Contains("ゲームフォルダが見つかりません", statusText, StringComparison.Ordinal);
        Assert.Equal(PatchScreenPlanner.RecheckLabel, mainButton);
        Assert.False(showSecondary);
        Assert.Contains("起動し直して", statusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(dialogs.Calls, call => call.StartsWith("Error: ", StringComparison.Ordinal));
        Assert.False(Directory.Exists(setup.GameDirectory));
    }

    [Fact]
    public void GameFolderRemovedBeforeOperation_StopsBeforeAnyConfirmation()
    {
        // AA-08: 表示が古いまま（日本語化する）ボタンが押されても、確認ダイアログを出す前に止め、起動し直すよう案内する（副作用なし）
        using var setup = new FakeGameSetup();
        var dialogs = new FakeDialogs();
        string? statusText = null;

        var unhandled = DispatcherRunner.Run(async () =>
        {
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), new FileLogger(setup.LogDirectory), dialogs, new FakeProcessChecker(GameProcessState.Unknown));
            vm.Refresh();
            Directory.Delete(setup.GameDirectory, recursive: true);

            vm.MainCommand.Execute(null);
            await DispatcherRunner.WaitUntil(() => !vm.IsOperationInProgress);
            statusText = vm.StatusText;
        });

        Assert.Empty(unhandled);
        Assert.DoesNotContain(dialogs.Calls, call => call.StartsWith("Confirm#", StringComparison.Ordinal));
        var warning = Assert.Single(dialogs.Calls, call => call.StartsWith("Warning: ", StringComparison.Ordinal));
        Assert.Contains("起動し直して", warning, StringComparison.Ordinal);
        Assert.Contains("ゲームフォルダが見つかりません", statusText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(setup.GameDirectory));
    }

    [Fact]
    public void TransientLogWriteFailure_IsReportedOnce_ThenLogLocationIsShownAgain()
    {
        // AA-09: 一度だけ書けなかった場合は「一部の記録をログファイルに書き込めませんでした」と知らせ、その後は通常どおりログの場所を案内する
        using var setup = new FakeGameSetup();
        var dialogs = new FakeDialogs();
        var logger = new FileLogger(setup.LogDirectory);
        string logText = string.Empty;

        var unhandled = DispatcherRunner.Run(() =>
        {
            var vm = new MainViewModel(setup.Installation, FakeGameSetup.Payload("0.1.0"), logger, dialogs, new FakeProcessChecker(GameProcessState.NotRunning));
            Directory.CreateDirectory(setup.LogDirectory);
            using (new FileStream(logger.CurrentLogPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                vm.Refresh();
            }

            vm.Refresh();
            vm.Refresh();
            logText = vm.LogText;
            return Task.CompletedTask;
        });

        Assert.Empty(unhandled);
        Assert.Single(logText.Split('\n'), line => line.Contains("一部の記録をログファイルに書き込めませんでした", StringComparison.Ordinal));
        Assert.DoesNotContain("以降の記録は", logText, StringComparison.Ordinal);
        Assert.Null(logger.LastError);
        Assert.Equal($"ログ: {logger.CurrentLogPath}", logger.DescribeLocation(logViewAvailable: true));
    }
}
