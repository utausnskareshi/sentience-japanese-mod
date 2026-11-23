using SentienceJP.Core.Platform;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Platform;

/// <summary>書き込み権限の確認・ログ出力・プロセス確認のテスト。</summary>
public sealed class PlatformTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WriteAccessChecker_WritableFolders_NoProblemsAndNoLeftovers()
    {
        var game = FakeGame.CreateGame(_temp.Combine("game"));
        var before = DirectorySnapshot.Capture(game);

        var problems = WriteAccessChecker.FindProblems(game, GameConstants.DirectoriesRequiringWriteAccess);

        Assert.Empty(problems);

        // 確認用の一時ファイルが残っていないこと
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(game)));
    }

    [Fact]
    public void WriteAccessChecker_MissingFolder_IsSkipped()
    {
        var problems = WriteAccessChecker.FindProblems(_temp.FullPath, ["not_exists"]);

        Assert.Empty(problems);
    }

    [Fact]
    public void FileLogger_AppendsLines()
    {
        var logger = new FileLogger(_temp.Combine("logs"));

        logger.Info("一行目");
        logger.Error("二行目", new InvalidOperationException("原因"));

        var text = File.ReadAllText(logger.CurrentLogPath);
        Assert.Contains("[INFO] 一行目", text, StringComparison.Ordinal);
        Assert.Contains("[ERROR] 二行目", text, StringComparison.Ordinal);
        Assert.Contains("原因", text, StringComparison.Ordinal);
        Assert.Null(logger.LastError);
    }

    [Fact]
    public void GameProcessChecker_UnrelatedFolder_IsNotRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // テスト用の一時フォルダで nw.exe が動いていることはないため、NotRunning か（他の nw.exe の場所が読めない場合）Unknown になる
        var state = new GameProcessChecker().Check(_temp.FullPath);

        Assert.NotEqual(GameProcessState.Running, state);
    }

    [Fact]
    public void WriteAccessAdvice_AccessDeniedWithoutElevation_OffersRestart()
    {
        var advice = WriteAccessChecker.Advise(
            [new WriteAccessProblem(string.Empty, "アクセスが拒否されました。", WriteAccessProblemKind.AccessDenied)],
            isElevated: false);

        Assert.True(advice.OfferRestartAsAdministrator);
        Assert.Contains("(ゲームフォルダ)", advice.Message, StringComparison.Ordinal);
        Assert.EndsWith("再起動しますか？", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAccessAdvice_AccessDeniedWhileElevated_ShowsOtherCausesInstead()
    {
        // A-05: 既に管理者なら、再起動しても解決しないため再起動を提案せず、コントロールされたフォルダー アクセス等を案内する
        var advice = WriteAccessChecker.Advise(
            [new WriteAccessProblem("www", "アクセスが拒否されました。", WriteAccessProblemKind.AccessDenied)],
            isElevated: true);

        Assert.False(advice.OfferRestartAsAdministrator);
        Assert.Contains("コントロールされたフォルダー アクセス", advice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("再起動しますか", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAccessAdvice_IOErrorOnly_IsNotAPermissionProblem()
    {
        // A-05: 容量不足などは権限の問題ではないため、「権限がありません」と案内しない
        var advice = WriteAccessChecker.Advise(
            [new WriteAccessProblem("www/js", "ディスクに十分な空き領域がありません。", WriteAccessProblemKind.IOError)],
            isElevated: false);

        Assert.False(advice.OfferRestartAsAdministrator);
        Assert.Contains("権限の問題ではありません", advice.Message, StringComparison.Ordinal);
        Assert.Contains("空き容量", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAccessAdvice_MixedProblemsWithoutElevation_OffersRestart()
    {
        var advice = WriteAccessChecker.Advise(
            [
                new WriteAccessProblem("www", "空き領域がありません。", WriteAccessProblemKind.IOError),
                new WriteAccessProblem("www/js", "アクセスが拒否されました。", WriteAccessProblemKind.AccessDenied),
            ],
            isElevated: false);

        Assert.True(advice.OfferRestartAsAdministrator);
        Assert.Contains("www/js", advice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_DescribeLocation_PointsToLogFile()
    {
        var logger = new FileLogger(_temp.Combine("logs"));
        logger.Info("一行目");

        Assert.Equal($"ログ: {logger.CurrentLogPath}", logger.DescribeLocation());
    }

    [Fact]
    public void FileLogger_WriteFailure_IsReportedInsteadOfMissingLogPath()
    {
        // A-07: ログを書けなかった場合は、存在しないログファイルを案内せず、書けなかったことを伝える
        var blocker = _temp.Combine("not_a_folder");
        File.WriteAllText(blocker, "ログフォルダの場所にファイルがある");
        var logger = new FileLogger(blocker);

        logger.Info("書けない");

        Assert.NotNull(logger.LastError);
        Assert.Contains("ログを書き込めませんでした", logger.DescribeLocation(), StringComparison.Ordinal);
        Assert.DoesNotContain("ログ欄", logger.DescribeLocation(), StringComparison.Ordinal);
        Assert.Contains("ログ欄", logger.DescribeLocation(logViewAvailable: true), StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_TransientFailure_IsClearedAfterNextSuccessfulWrite()
    {
        // CA-17: 一時的に書けなかった後に書けるようになったら、失敗の記録を消し、以降は通常どおりログの場所を案内する
        var directory = _temp.Combine("logs");
        File.WriteAllText(directory, "一時的にフォルダの場所にファイルがある");
        var logger = new FileLogger(directory);
        logger.Info("失敗する");
        Assert.NotNull(logger.LastError);

        File.Delete(directory);
        logger.Info("成功する");

        Assert.Null(logger.LastError);
        Assert.Equal($"ログ: {logger.CurrentLogPath}", logger.DescribeLocation());
        Assert.Contains("成功する", File.ReadAllText(logger.CurrentLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_BrieflyLockedLogFile_IsAbsorbedByRetry()
    {
        // CA-17: 別のプロセスがログファイルを一瞬開いている（共有違反）だけなら、短い再試行で吸収して失敗にしない
        var logger = new FileLogger(_temp.Combine("logs"));
        logger.Info("一行目");

        using (BriefFileLock.Hold(logger.CurrentLogPath, FileShare.None, TimeSpan.FromMilliseconds(60)))
        {
            logger.Info("ロック中に書いた行");
        }

        Assert.Null(logger.LastError);
        Assert.Contains("ロック中に書いた行", File.ReadAllText(logger.CurrentLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void SingleInstanceLock_SecondAcquireFails_UntilReleased()
    {
        // A-08: 2 つ目の起動ではロックを取得できず、1 つ目が終了（解放）すれば取得できる
        var name = $@"Local\SentienceJP.Tests.{Guid.NewGuid():N}";
        var first = SingleInstanceLock.TryAcquire(name, TimeSpan.Zero);
        Assert.NotNull(first);

        Assert.False(CanAcquireOnOtherThread(name));
        first.Dispose();
        first.Dispose();
        Assert.True(CanAcquireOnOtherThread(name));
    }

    [Fact]
    public void SingleInstanceLock_AbandonedByCrashedInstance_CanBeAcquired()
    {
        // 前のインスタンスが解放せずに終了した（強制終了など）場合も、次の起動でロックを取得できる
        var name = $@"Local\SentienceJP.Tests.{Guid.NewGuid():N}";
        var thread = new Thread(() => SingleInstanceLock.TryAcquire(name, TimeSpan.Zero));
        thread.Start();
        thread.Join();

        using var again = SingleInstanceLock.TryAcquire(name, TimeSpan.FromSeconds(5));

        Assert.NotNull(again);
    }

    [Fact]
    public void SingleInstanceLock_AccessDeniedWithTimeout_RetriesUntilTheMutexCanBeOpened()
    {
        // AA-11: 管理者としての再起動（待ち時間あり）では、既存の Mutex を開く権限が無くても、待ち時間の間は一定間隔で開き直す
        // （本物の名前は使わない。権限の無い状況は、開く処理が UnauthorizedAccessException を投げることで模擬する）
        var name = $@"Local\SentienceJP.Tests.{Guid.NewGuid():N}";
        var attempts = 0;

        using var acquired = SingleInstanceLock.TryAcquire(
            name,
            TimeSpan.FromSeconds(10),
            mutexName =>
            {
                attempts++;
                return attempts < 3 ? throw new UnauthorizedAccessException("拒否") : new Mutex(false, mutexName);
            },
            TimeSpan.FromMilliseconds(10));

        Assert.NotNull(acquired);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void SingleInstanceLock_AccessDeniedWithoutTimeout_GivesUpImmediately()
    {
        // AA-11: 通常の起動（待ち時間 0）は、従来どおりアクセス拒否で即座に「既に起動している」と判定する
        var attempts = 0;

        var acquired = SingleInstanceLock.TryAcquire(
            $@"Local\SentienceJP.Tests.{Guid.NewGuid():N}",
            TimeSpan.Zero,
            _ =>
            {
                attempts++;
                throw new UnauthorizedAccessException("拒否");
            },
            TimeSpan.FromMilliseconds(10));

        Assert.Null(acquired);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void SingleInstanceLock_AccessDeniedForWholeTimeout_GivesUpAfterTimeout()
    {
        // AA-11: 待ち時間を使い切っても開けなければ、あきらめて「既に起動している」と判定する
        var attempts = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var acquired = SingleInstanceLock.TryAcquire(
            $@"Local\SentienceJP.Tests.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(200),
            _ =>
            {
                attempts++;
                throw new UnauthorizedAccessException("拒否");
            },
            TimeSpan.FromMilliseconds(20));

        Assert.Null(acquired);
        Assert.True(attempts >= 2, $"開き直した回数: {attempts}");
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150), $"経過時間: {stopwatch.Elapsed}");
    }

    [Theory]
    [InlineData(0, 0, 200, null)]
    [InlineData(10000, 0, 200, 200)]
    [InlineData(10000, 9900, 200, 100)]
    [InlineData(10000, 10000, 200, null)]
    [InlineData(10000, 12000, 200, null)]
    public void SingleInstanceLock_NextAccessDeniedRetryDelay(int timeoutMs, int elapsedMs, int intervalMs, int? expectedMs)
    {
        var delay = SingleInstanceLock.NextAccessDeniedRetryDelay(
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(elapsedMs),
            TimeSpan.FromMilliseconds(intervalMs));

        Assert.Equal(expectedMs is null ? null : TimeSpan.FromMilliseconds(expectedMs.Value), delay);
    }

    [Theory]
    [InlineData(new string[0], false, null, false)]
    [InlineData(new[] { "--verify-payload" }, true, null, false)]
    [InlineData(new[] { "--verify-payload", @"C:\work dir\report.txt" }, true, @"C:\work dir\report.txt", false)]
    [InlineData(new[] { "--VERIFY-PAYLOAD", "--elevated-restart" }, true, null, true)]
    [InlineData(new[] { "--elevated-restart" }, false, null, true)]
    [InlineData(new[] { "unknown", "--verify-payload", " " }, true, null, false)]
    public void PatcherCommandLine_Parse(string[] args, bool verify, string? reportPath, bool elevatedRestart)
    {
        var commandLine = PatcherCommandLine.Parse(args);

        Assert.Equal(new PatcherCommandLine(verify, reportPath, elevatedRestart), commandLine);
    }

    /// <summary>別のスレッドでロックの取得を試み、取得できたかを返す（取得できた場合はそのスレッドで解放する）。</summary>
    private static bool CanAcquireOnOtherThread(string name)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            using var instanceLock = SingleInstanceLock.TryAcquire(name, TimeSpan.Zero);
            acquired = instanceLock is not null;
        });
        thread.Start();
        thread.Join();
        return acquired;
    }
}
