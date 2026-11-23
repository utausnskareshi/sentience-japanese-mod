using System.Text;
using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Plugins;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// ファイル操作に失敗・中断を注入して、適用・復元の頑健性を確かめる結合テスト（偽ゲームフォルダで実行する）。
/// - 各段階で失敗したときに、ロールバックで適用前とバイト単位（属性を含む）で一致する状態へ戻ること
/// - 取り消しの一部に失敗したときに、バックアップと状態ファイルを残し、後から「元に戻す」で片付けられること
/// - プロセスの強制終了（電源断）でどの段階から中断しても、「元に戻す」で完全に元通りにでき、再適用もできること
/// - 適用中に他のプログラムが plugins.js を書き換えた場合に、その内容を消さないこと
/// </summary>
public sealed class PatchFailureInjectionTests : IDisposable
{
    /// <summary>
    /// 偽ゲームへの適用で成功する書き込み系操作の回数
    /// （バックアップ用フォルダ 2・バックアップ 1・追加ファイル 3・追加フォルダ 2・plugins.js 置換 1・状態ファイル 1）。
    /// </summary>
    private const int ApplyMutationCount = 10;

    /// <summary>偽ゲームの復元で成功する書き込み系操作の回数（plugins.js 1・追加ファイル 3・フォルダ 2・バックアップフォルダ 1）。</summary>
    private const int RestoreMutationCount = 7;

    private readonly TempDirectory _temp = new();
    private readonly string _game;
    private readonly PayloadBundle _payload;
    private readonly List<string> _log = [];

    /// <summary>偽のゲームとペイロードを用意する。</summary>
    public PatchFailureInjectionTests()
    {
        _game = FakeGame.CreateGame(_temp.Combine("game"));
        _payload = FakeGame.CreatePayload(_temp.Combine("payload"));
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>適用の各段階（失敗させる操作の種類と対象）。</summary>
    public static TheoryData<FileOperationKind, string> ApplyStages() => new()
    {
        { FileOperationKind.CreateDirectory, "sentience_jp_backup" },
        { FileOperationKind.CreateDirectory, "sentience_jp_backup/original/www/js" },
        { FileOperationKind.WriteNewFile, "sentience_jp_backup/original/www/js/plugins.js" },
        { FileOperationKind.Read, "sentience_jp_backup/original/www/js/plugins.js" },
        { FileOperationKind.WriteNewFile, "www/js/plugins/SentienceJP.js" },
        { FileOperationKind.CreateDirectory, "www/sentience_jp" },
        { FileOperationKind.WriteNewFile, "www/sentience_jp/translation.json" },
        { FileOperationKind.CreateDirectory, "www/sentience_jp/fonts" },
        { FileOperationKind.WriteNewFile, "www/sentience_jp/fonts/readme.txt" },
        { FileOperationKind.ReplaceFile, "www/js/plugins.js" },
        { FileOperationKind.ReplaceFile, "sentience_jp_backup/state.json" },
    };

    [Theory]
    [MemberData(nameof(ApplyStages))]
    public void Apply_FailureAtEachStage_RollsBackToByteIdenticalTree(FileOperationKind kind, string relativePath)
    {
        var before = DirectorySnapshot.Capture(_game);
        var fileSystem = new FaultInjectingFileSystem { FailWhen = (k, path, _) => k == kind && IsPath(path, relativePath) };

        var result = Apply(fileSystem);

        Assert.False(result.Success);
        Assert.IsType<InjectedFailureException>(result.Error);
        Assert.Empty(result.CleanupErrors);
        Assert.DoesNotContain("取り消せません", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
        Assert.Equal(PatchStatusKind.NotApplied, Detect().Kind);
    }

    [Fact]
    public void Apply_VerificationFails_RollsBackAndSaysSo()
    {
        var before = DirectorySnapshot.Capture(_game);

        // 状態ファイルを書いた直後（検証の直前）に、ウイルス対策ソフト等が追加ファイルを隔離した状況
        var fileSystem = new FaultInjectingFileSystem
        {
            After = (kind, path) =>
            {
                if (kind == FileOperationKind.ReplaceFile && IsPath(path, BackupLayout.StateFile))
                {
                    File.Delete(Full("www/sentience_jp/translation.json"));
                }
            },
        };

        var result = Apply(fileSystem);

        Assert.False(result.Success);
        Assert.Contains("適用後の検証に失敗しました", result.Message, StringComparison.Ordinal);
        Assert.Contains("元の状態に戻しました", result.Message, StringComparison.Ordinal);
        Assert.Empty(result.CleanupErrors);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Apply_UndoOfPluginsJsFails_KeepsBackupAndState_ThenRestoreWorks()
    {
        // C-09: 取り消しで plugins.js を戻せなかった場合は、バックアップフォルダ（元の plugins.js と状態ファイル）を消さずに残す
        var before = DirectorySnapshot.Capture(_game);
        var fileSystem = new FaultInjectingFileSystem
        {
            After = (kind, path) =>
            {
                if (kind == FileOperationKind.ReplaceFile && IsPath(path, BackupLayout.StateFile))
                {
                    File.Delete(Full("www/sentience_jp/translation.json"));
                }
            },

            // plugins.js の 2 回目以降の置き換え（＝取り消し）だけを失敗させる
            FailWhen = (kind, path, occurrence) =>
                kind == FileOperationKind.ReplaceFile && IsPath(path, GameConstants.PluginsJsRelativePath) && occurrence >= 2,
        };

        var result = Apply(fileSystem);

        Assert.False(result.Success);
        Assert.NotEmpty(result.CleanupErrors);
        Assert.Contains("取り消せませんでした", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Full(BackupLayout.StateFile)));
        Assert.True(File.Exists(Full(BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath))));
        Assert.Contains(_log, line => line.Contains("バックアップを残します", StringComparison.Ordinal));
        Assert.NotEqual(PatchStatusKind.NotApplied, Detect().Kind);

        var restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(restored.Success, restored.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Apply_PluginsJsReplacedBySteamBeforeVerification_KeepsSteamVersion()
    {
        // C-01: 適用中に Steam の更新が plugins.js を新しい版（パッチのエントリなし）に置き換えた場合、ロールバックで古い内容を書き戻さない
        var before = DirectorySnapshot.Capture(_game);
        var steamNew = FakeGame.SamplePluginsJs.Replace(
            "\n];",
            ",\n{\"name\":\"NewOfficialPlugin\",\"status\":true,\"description\":\"\",\"parameters\":{}}\n];",
            StringComparison.Ordinal);
        var fileSystem = new FaultInjectingFileSystem
        {
            After = (kind, path) =>
            {
                if (kind == FileOperationKind.ReplaceFile && IsPath(path, BackupLayout.StateFile))
                {
                    File.WriteAllText(Full(GameConstants.PluginsJsRelativePath), steamNew, new UTF8Encoding(false));
                }
            },
        };

        var result = Apply(fileSystem);

        Assert.False(result.Success);
        Assert.Empty(result.CleanupErrors);
        Assert.Equal(steamNew, File.ReadAllText(Full(GameConstants.PluginsJsRelativePath)));
        var diff = DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game));
        Assert.Equal(GameConstants.PluginsJsRelativePath, Assert.Single(diff).Split(':')[0]);
        Assert.Equal(PatchStatusKind.NotApplied, Detect().Kind);
    }

    [Fact]
    public void Apply_OtherModEditedPluginsJsBeforeVerification_RemovesOnlyOwnEntry()
    {
        // C-01: 適用中に別の MOD が plugins.js に要素を追加した場合、取り消しはパッチのエントリだけを取り除く
        var other = "{\"name\":\"OtherMod\",\"status\":true,\"description\":\"\",\"parameters\":{}}";
        var fileSystem = new FaultInjectingFileSystem
        {
            After = (kind, path) =>
            {
                if (kind == FileOperationKind.ReplaceFile && IsPath(path, BackupLayout.StateFile))
                {
                    var plugins = Full(GameConstants.PluginsJsRelativePath);
                    File.WriteAllText(plugins, PluginsJsEditor.InsertEntry(File.ReadAllText(plugins), other), new UTF8Encoding(false));
                    File.Delete(Full("www/sentience_jp/translation.json"));
                }
            },
        };

        var result = Apply(fileSystem);

        Assert.False(result.Success);
        Assert.Empty(result.CleanupErrors);
        Assert.Equal(PluginsJsEditor.InsertEntry(FakeGame.SamplePluginsJs, other), File.ReadAllText(Full(GameConstants.PluginsJsRelativePath)));
        Assert.False(Directory.Exists(Full(GameConstants.BackupDirectoryName)));
        Assert.False(Directory.Exists(Full(GameConstants.PatchDataDirectoryRelativePath)));
    }

    [Fact]
    public void Apply_PluginsJsBrieflyOpenedByScanner_SucceedsByRetryingReplacement()
    {
        // CA-03: plugins.js の置き換えの直前から 150ms ほど他のプログラムが plugins.js を開いていても、
        // 置き換えの段を既定の待ち時間で再試行して適用が成功する（以前はアクセス拒否で失敗しロールバックされていた）
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        using var opened = new ManualResetEventSlim();
        Thread? holder = null;
        var fileSystem = new FaultInjectingFileSystem(FileOperations.Default)
        {
            After = (kind, path) =>
            {
                if (kind == FileOperationKind.WriteNewFile && path.EndsWith("readme.txt", StringComparison.OrdinalIgnoreCase))
                {
                    holder = new Thread(() =>
                    {
                        using var stream = new FileStream(plugins, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        opened.Set();
                        Thread.Sleep(150);
                    });
                    holder.Start();
                    opened.Wait();
                }
            },
        };

        var result = Apply(fileSystem);
        holder?.Join();

        Assert.True(result.Success, result.Message);
        Assert.NotNull(holder);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(ApplyMutationCount)]
    public void Apply_InterruptedAtAnyStep_RestoreReturnsToByteIdenticalTree_AndReapplyWorks(int completedMutations)
    {
        // プロセスの強制終了（電源断）を模擬: 指定回数の書き込みの後は、取り消しも含めて何も実行できない
        var before = DirectorySnapshot.Capture(_game);
        var crashing = new FaultInjectingFileSystem { CrashAfterMutations = completedMutations };

        var result = Apply(crashing);

        // 再起動後の状態判定: 何も書き込まれていなければ未適用、痕跡があれば「不整合」（日本語化ボタンを出さず、片付けを促す）
        var status = Detect();
        if (completedMutations == 0)
        {
            Assert.False(result.Success);
            Assert.Equal(PatchStatusKind.NotApplied, status.Kind);
        }
        else if (completedMutations == ApplyMutationCount)
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(PatchStatusKind.Applied, status.Kind);
        }
        else
        {
            Assert.False(result.Success);
            Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
            Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);
        }

        var restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);
        Assert.True(restored.Success, restored.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));

        // 片付けた後は、通常どおり適用 → 元に戻すができる
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(RestoreMutationCount)]
    public void Restore_InterruptedAtAnyStep_RerunReturnsToByteIdenticalTree(int completedMutations)
    {
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var crashing = new FaultInjectingFileSystem { CrashAfterMutations = completedMutations };

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add, crashing);

        Assert.Equal(completedMutations == RestoreMutationCount, result.Success);
        var status = Detect();
        if (completedMutations >= 4 && completedMutations < RestoreMutationCount)
        {
            // C-10: エントリも追加ファイルも無く状態ファイルだけが残った場合は「再適用」ではなく「元に戻す」を主操作にする
            Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
            Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);
        }

        var rerun = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(rerun.Success, rerun.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Restore_StoppedWithOnlyStateLeft_CleansUp()
    {
        // 復元の最後（バックアップフォルダの削除）が途中で止まり、状態ファイルだけが残った状況（E7b）
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var crashing = new FaultInjectingFileSystem { CrashAfterMutations = RestoreMutationCount - 1 };
        Assert.False(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add, crashing).Success);
        File.Delete(Full(BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath)));
        Assert.Equal(PatchStatusKind.Inconsistent, Detect().Kind);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void LeftoverTemporaryFileFromInterruptedApply_IsDetectedAndCleanedUp()
    {
        // 適用中、plugins.js の一時ファイルを書いた直後（置き換え前）に中断した状況（E6b）:
        // バックアップ・追加ファイルあり、plugins.js は元のまま、一時ファイルに適用後の内容、状態ファイルなし
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        File.WriteAllBytes(plugins + GameConstants.TemporaryFileSuffix, File.ReadAllBytes(plugins));
        File.Copy(Full(BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath)), plugins, overwrite: true);
        File.Delete(Full(BackupLayout.StateFile));

        var status = Detect();
        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Contains(status.Reasons, r => r.Contains(GameConstants.TemporaryFileSuffix, StringComparison.Ordinal));

        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void LeftoverTemporaryFileWhileApplied_IsRemovedByRestore()
    {
        // 適用済みのまま一時ファイルだけが残った場合（元に戻す処理の置き換え中に中断した等）も、元に戻すで片付く（E6）
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.WriteAllText(Full(GameConstants.PluginsJsRelativePath) + GameConstants.TemporaryFileSuffix, "partial");

        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);

        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    /// <summary>失敗注入つきのファイル操作で適用する。</summary>
    private PatchOperationResult Apply(FaultInjectingFileSystem fileSystem) =>
        PatchApplier.Apply(_game, null, _payload, _log.Add, TimeProvider.System, fileSystem);

    /// <summary>フルパスが、ゲームルートからの相対パスと一致するか。</summary>
    private bool IsPath(string fullPath, string relativePath) =>
        string.Equals(Path.GetFullPath(fullPath), Full(relativePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>ゲームフォルダ配下のフルパス。</summary>
    private string Full(string relativePath) => GamePaths.ToFullPath(_game, relativePath);

    /// <summary>現在の状態を判定する。</summary>
    private PatchStatus Detect() => PatchStatusDetector.Detect(GameSnapshotReader.Read(_game, _payload.Manifest), _payload.Manifest);
}
