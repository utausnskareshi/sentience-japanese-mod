using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// 異常な状態（plugins.js の破損、バックアップの不一致、未対応の記録、想定外のファイル、読み取り専用属性）からの
/// 復元を確かめる結合テスト（偽ゲームフォルダで実行する）。
/// </summary>
public sealed class PatchRecoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _game;
    private readonly PayloadBundle _payload;
    private readonly List<string> _log = [];

    /// <summary>偽のゲームとペイロードを用意する。</summary>
    public PatchRecoveryTests()
    {
        _game = FakeGame.CreateGame(_temp.Combine("game"));
        _payload = FakeGame.CreatePayload(_temp.Combine("payload"));
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CorruptPluginsJsWithValidBackup_RestoreUsesBackup()
    {
        // C-04: 電源断などで plugins.js がゼロ埋めになっても、記録と一致するバックアップがあれば元に戻せる（E13）
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        CorruptPluginsJs();

        var status = Detect();
        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Contains(_log, line => line.Contains("解析できないため", StringComparison.Ordinal));
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void CorruptPluginsJsWithValidBackup_ReapplyAlsoWorks()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        CorruptPluginsJs();

        var result = PatchOperations.Execute(PatchAction.Reapply, _game, null, _payload, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);
    }

    [Fact]
    public void CorruptPluginsJsWithoutBackup_RestoreFailsWithGuidanceAndChangesNothing()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.Delete(Full(BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath)));
        CorruptPluginsJs();
        var corrupted = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("整合性を確認", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(corrupted, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void UnparsablePluginsJsOnApply_FailsWithGuidance()
    {
        // C-04: 形式エラーは対処方法の案内つきで伝える（何も書き換えない）
        FakeGame.WriteText(_game, GameConstants.PluginsJsRelativePath, "var $plugins =\n[\n{\"name\":\"A\"},\n];\n");
        var before = DirectorySnapshot.Capture(_game);

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("整合性を確認", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void BackupNotMatchingRecord_RestoreRemovesEntryTextually()
    {
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.WriteAllText(Full(BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath)), "// 壊れたバックアップ");

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Contains(_log, line => line.Contains("記録と一致しない", StringComparison.Ordinal));
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void StateWithUnsupportedModifiedFile_RestoreStopsWithoutChanges()
    {
        // C-11: 将来の版が plugins.js 以外も変更・バックアップした記録は、この版では戻し方が分からないため、何も変更せずに中止する（E28）
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        FakeGame.WriteText(_game, "www/index.html", "<html>patched</html>");
        FakeGame.WriteText(_game, BackupLayout.BackupPathFor("www/index.html"), "<html>original</html>");
        var statePath = Full(BackupLayout.StateFile);
        var state = PatchStateSerializer.Deserialize(File.ReadAllBytes(statePath));
        var extended = state with
        {
            ModifiedFiles =
            [
                .. state.ModifiedFiles,
                new ModifiedFileRecord("www/index.html", Hashing.Sha256Hex("<html>original</html>"u8), Hashing.Sha256Hex("<html>patched</html>"u8), BackupLayout.BackupPathFor("www/index.html")),
            ],
        };
        File.WriteAllBytes(statePath, PatchStateSerializer.Serialize(extended));
        var beforeRestore = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("www/index.html", result.Message, StringComparison.Ordinal);
        Assert.Contains("このバージョンのツールでは元に戻せない", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(beforeRestore, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void UnknownFileInBackupFolder_IsKeptAndReported()
    {
        // バックアップフォルダに、このツールが作っていないファイル（新しい版のバックアップ等）があれば、削除せずに残して知らせる
        var original = File.ReadAllBytes(Full(GameConstants.PluginsJsRelativePath));
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        FakeGame.WriteText(_game, "sentience_jp_backup/original/www/index.html", "<html>original</html>");

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("sentience_jp_backup/original/www/index.html", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Full("sentience_jp_backup/original/www/index.html")));
        Assert.Equal(original, File.ReadAllBytes(Full(GameConstants.PluginsJsRelativePath)));
        Assert.False(Directory.Exists(Full(GameConstants.PatchDataDirectoryRelativePath)));
    }

    [Fact]
    public void UserFolderWithAliasNameInPatchFolder_IsLeftUntouched()
    {
        // 利用者がパッチのフォルダ内に、パッチが扱わない名前（8.3 形式に見える名前など）のフォルダを作っていても、
        // 復元は止まらず、そのフォルダ（と親フォルダ）を残す
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var odd = Path.Combine(Full(GameConstants.PatchDataDirectoryRelativePath), "odd~1");
        Directory.CreateDirectory(odd);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(odd));
    }

    [Fact]
    public void ReadOnlyPluginsJs_ApplyAndRestoreSucceed_AndKeepAttributes()
    {
        // C-02: 読み取り専用（＋隠し）の plugins.js でも適用・復元でき、属性も元どおりに戻る
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        File.SetAttributes(plugins, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        var before = DirectorySnapshot.Capture(_game);

        var applied = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.True(applied.Success, applied.Message);
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive, File.GetAttributes(plugins) & FileOperations.PreservedAttributes);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);

        var restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(restored.Success, restored.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginsJsMadeReadOnlyAfterApply_RestoreSucceeds()
    {
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        File.SetAttributes(plugins, File.GetAttributes(plugins) | FileAttributes.ReadOnly);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);

        // 内容は適用前に戻り、読み取り専用属性は（復元の直前の状態として）保たれる
        Assert.True(File.GetAttributes(plugins).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(plugins, File.GetAttributes(plugins) & ~FileAttributes.ReadOnly);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void ReadOnlyAddedFilesFoldersAndBackup_RestoreSucceeds()
    {
        // C-02: 追加ファイル・追加フォルダ・バックアップに読み取り専用属性が付いても（同期ソフトや手作業など）、復元で削除できる
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        foreach (var root in new[] { Full(GameConstants.PatchDataDirectoryRelativePath), Full(GameConstants.BackupDirectoryName) })
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }

            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
            {
                new DirectoryInfo(directory).Attributes |= FileAttributes.ReadOnly;
            }
        }

        var plugin = Full(GameConstants.PluginFileRelativePath);
        File.SetAttributes(plugin, File.GetAttributes(plugin) | FileAttributes.ReadOnly);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginsJsDeletedWithValidBackup_RestoreRebuildsFromBackup()
    {
        // CA-05: 状態ファイルと正しいバックアップがあれば、plugins.js が消えても（隔離・誤削除）元に戻すでバックアップから作り直せる
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.Delete(Full(GameConstants.PluginsJsRelativePath));

        var status = Detect();
        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);
        Assert.Contains(status.Reasons, r => r.Contains("plugins.js がありません", StringComparison.Ordinal));
        Assert.Contains(status.Reasons, r => r.Contains("バックアップから plugins.js を作り直します", StringComparison.Ordinal));

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Contains(_log, line => line.Contains("作り直しています", StringComparison.Ordinal));
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginsJsDeletedWithValidBackup_ReapplyAlsoWorks()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.Delete(Full(GameConstants.PluginsJsRelativePath));

        var result = PatchOperations.Execute(PatchAction.Reapply, _game, null, _payload, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PluginsJsDeletedWithoutUsableBackup_RestoreFailsWithGuidanceAndChangesNothing(bool deleteBackup)
    {
        // CA-05: 使えるバックアップが無ければ、理由と Steam の整合性確認の案内つきで、何も変更せずに中止する
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.Delete(Full(GameConstants.PluginsJsRelativePath));
        var backup = Full(BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath));
        if (deleteBackup)
        {
            File.Delete(backup);
        }
        else
        {
            File.WriteAllText(backup, "// 記録と一致しないバックアップ");
        }

        var before = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.False(result.Success);
        Assert.IsType<PatchException>(result.Error);
        Assert.Contains("plugins.js が無く", result.Message, StringComparison.Ordinal);
        Assert.Contains("整合性を確認", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginsJsLockedByOtherProgram_IsNotTreatedAsMissing()
    {
        // CA-05: ロックで読めないだけの plugins.js を「無い」とみなしてバックアップで上書きしない（状態の文言も「読み取れません」にする）
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        var patched = File.ReadAllBytes(plugins);

        using (new FileStream(plugins, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var status = Detect();
            Assert.Contains(status.Reasons, r => r.Contains("読み取れません", StringComparison.Ordinal));
            Assert.DoesNotContain(status.Reasons, r => r.Contains("plugins.js がありません", StringComparison.Ordinal));

            var locked = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

            Assert.False(locked.Success);
            Assert.DoesNotContain(_log, line => line.Contains("作り直しています", StringComparison.Ordinal));
        }

        Assert.Equal(patched, File.ReadAllBytes(plugins));
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginsJsMissingOnApply_FailsWithClearMessageAndChangesNothing()
    {
        // CA-05: 適用も、plugins.js が無ければ分かりやすく失敗する（何も書き込まない）
        File.Delete(Full(GameConstants.PluginsJsRelativePath));
        var before = DirectorySnapshot.Capture(_game);

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("plugins.js が無いため、日本語化できません", result.Message, StringComparison.Ordinal);
        Assert.Contains("整合性を確認", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginsJsDeletedDuringApply_RollbackRecreatesOriginal()
    {
        // CA-05: 適用中（状態ファイル記録の直後）に plugins.js が消えた場合、取り消しで適用前の内容から作り直してバイト一致に戻す
        var before = DirectorySnapshot.Capture(_game);
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        var fileSystem = new FaultInjectingFileSystem
        {
            After = (kind, path) =>
            {
                if (kind == FileOperationKind.ReplaceFile && path.EndsWith(GameConstants.StateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(plugins);
                }
            },
        };

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add, TimeProvider.System, fileSystem);

        Assert.False(result.Success);
        Assert.Empty(result.CleanupErrors);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void AddedFileBrieflyLockedByScanner_StatusAndRestoreStillSucceed()
    {
        // CA-11: 追加ファイルを他のプログラムが一瞬（150ms）排他で開いていても、状態判定（ハッシュの計算）は再試行して成功し、
        // 元に戻すも失敗しない（以前は状態判定が IOException になり「状態を確認できませんでした」になっていた）
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var translation = Full("www/sentience_jp/translation.json");

        PatchStatus status;
        using (BriefFileLock.Hold(translation, FileShare.None, TimeSpan.FromMilliseconds(150)))
        {
            status = Detect();
        }

        PatchOperationResult restored;
        using (BriefFileLock.Hold(translation, FileShare.None, TimeSpan.FromMilliseconds(150)))
        {
            restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);
        }

        Assert.Equal(PatchStatusKind.Applied, status.Kind);
        Assert.True(restored.Success, restored.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Theory]
    [InlineData("sentience_jp_backup/state.json")]
    [InlineData("www/js/plugins.js")]
    public void StateOrPluginsJsBrieflyLocked_StatusIsReadByRetrying(string relativePath)
    {
        // CA-11: state.json・plugins.js の読み取りも共有違反を再試行する（短いロックで「壊れている」「読み取れない」と誤判定しない）
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        PatchStatus status;
        using (BriefFileLock.Hold(Full(relativePath), FileShare.None, TimeSpan.FromMilliseconds(150)))
        {
            status = Detect();
        }

        Assert.Equal(PatchStatusKind.Applied, status.Kind);
    }

    [Fact]
    public void SnapshotWithoutRetry_ReportsLockedPluginsJsAsUnreadable()
    {
        // CA-11（対照）: 再試行しない方針で読めば、ロック中の plugins.js は「読めない」になる（＝通常の読み取りは再試行を使っていることの確認）
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        GameSnapshot snapshot;
        using (BriefFileLock.Hold(Full(GameConstants.PluginsJsRelativePath), FileShare.None, TimeSpan.FromMilliseconds(300)))
        {
            snapshot = GameSnapshotReader.Read(_game, _payload.Manifest, SharingViolationRetry.None);
        }

        Assert.Equal(PluginsJsProblemKind.Unreadable, snapshot.EffectivePluginsJsProblem);
    }

    [Fact]
    public void PreexistingEmptyPatchFolder_IsKeptByRestore_ByteIdentical()
    {
        // CA-15: 適用前から空の www/sentience_jp があっても、状態ファイルがある復元では記録したフォルダ（新しく作ったもの）だけを消すため、バイト一致に戻る
        Directory.CreateDirectory(Full(GameConstants.PatchDataDirectoryRelativePath));
        var before = DirectorySnapshot.Capture(_game);
        Assert.Equal(PatchStatusKind.NotApplied, Detect().Kind);

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var state = PatchStateSerializer.Deserialize(File.ReadAllBytes(Full(BackupLayout.StateFile)));
        Assert.Equal(["www/sentience_jp/fonts"], state.CreatedDirectories);
        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void UserEmptySubfolderInPatchFolder_WithState_IsLeftUntouched()
    {
        // CA-15: 状態ファイルがある復元では、記録に無いフォルダ（利用者が作った空のサブフォルダ）は消さない（その親も空にならないため残る）
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var userFolder = Full("www/sentience_jp/user_folder");
        Directory.CreateDirectory(userFolder);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.True(Directory.Exists(userFolder));
        Assert.False(Directory.Exists(Full("www/sentience_jp/fonts")), "記録したフォルダは消す");
    }

    [Fact]
    public void PatchFolderLeftoversWithoutState_AreCleanedUpAsBefore()
    {
        // CA-15: 状態ファイルが無い片付けの経路は従来どおり、パッチ専用フォルダとその配下の空フォルダを消す
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Directory.CreateDirectory(Full("www/sentience_jp/fonts/empty_leftover"));
        Directory.Delete(Full(GameConstants.BackupDirectoryName), recursive: true);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void GameFolderRemovedAfterDetection_IsGameNotFound_AndOperationsChangeNothing()
    {
        // AA-08: 画面を開いたままゲームフォルダが無くなった（アンインストール・移動）場合は「不整合」ではなく「見つからない」とし、
        // 主ボタンは再確認。元に戻す・日本語化は何も作らずに、起動し直すよう案内する
        Directory.Delete(_game, recursive: true);

        var status = Detect();
        var plan = PatchOperations.PlanFor(status);
        var restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);
        var applied = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.Equal(PatchStatusKind.GameNotFound, status.Kind);
        Assert.Contains(PatchMessages.GameNotFoundGuidance, status.Reasons);
        Assert.Null(plan.MainAction);
        Assert.Equal(PatchScreenPlanner.RecheckLabel, plan.MainLabel);
        Assert.False(plan.ShowSecondaryRestore);
        Assert.False(restored.Success);
        Assert.Contains("起動し直して", restored.Message, StringComparison.Ordinal);
        Assert.False(applied.Success);
        Assert.Contains("起動し直して", applied.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_game), "フォルダを作らない（副作用なし）");
    }

    [Fact]
    public void GameUninstalledLeavingOnlyPatchFiles_IsGameNotFound_AndRestoreChangesNothing()
    {
        // AA-08: アンインストール後に、Steam が消さないパッチのファイルだけが残った（必須ファイルが 1 つも無い）場合も「見つからない」にする
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        foreach (var required in GameConstants.RequiredGameFiles)
        {
            File.Delete(Full(required));
        }

        var before = DirectorySnapshot.Capture(_game);

        var status = Detect();
        var restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.Equal(PatchStatusKind.GameNotFound, status.Kind);
        Assert.False(restored.Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Theory]
    [InlineData("nw.exe")]
    [InlineData("www/js/plugins.js")]
    public void OneRequiredFileMissing_IsNotGameNotFound(string missing)
    {
        // AA-08: 必須ファイルの一部だけが無い場合（plugins.js の消失・nw.exe の隔離など）は「見つからない」にしない（CA-05 などで扱う）
        File.Delete(Full(missing));

        Assert.NotEqual(PatchStatusKind.GameNotFound, Detect().Kind);
        Assert.Null(GameValidator.DescribeMissingGame(_game));
    }

    /// <summary>plugins.js を同じ長さのゼロ埋めにする（電源断で中身が失われた状況の模擬）。</summary>
    private void CorruptPluginsJs()
    {
        var plugins = Full(GameConstants.PluginsJsRelativePath);
        File.WriteAllBytes(plugins, new byte[new FileInfo(plugins).Length]);
    }

    /// <summary>ゲームフォルダ配下のフルパス。</summary>
    private string Full(string relativePath) => GamePaths.ToFullPath(_game, relativePath);

    /// <summary>現在の状態を判定する。</summary>
    private PatchStatus Detect() => PatchStatusDetector.Detect(GameSnapshotReader.Read(_game, _payload.Manifest), _payload.Manifest);
}
