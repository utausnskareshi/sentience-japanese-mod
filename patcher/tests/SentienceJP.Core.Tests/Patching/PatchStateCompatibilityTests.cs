using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Plugins;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// 状態ファイルの「破損」と「未対応（新しいバージョンのツールで適用された）」を区別することを確かめる結合テスト（CA-04）。
/// - 未対応: 状態は「新しいバージョンのツールで適用されています」、主ボタンは再確認。適用も復元も何も変更せずに中止する。
/// - 破損（長さ 0・BOM 付き）: 従来どおり「不整合」として、元に戻すでバイト一致に戻る。
/// </summary>
public sealed class PatchStateCompatibilityTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _game;
    private readonly PayloadBundle _payload;
    private readonly List<string> _log = [];

    /// <summary>偽のゲームとペイロードを用意する。</summary>
    public PatchStateCompatibilityTests()
    {
        _game = FakeGame.CreateGame(_temp.Combine("game"));
        _payload = FakeGame.CreatePayload(_temp.Combine("payload"));
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NewerSchemaStateWithUnsupportedRecord_RestoreAbortsWithoutAnyChange()
    {
        // 新しい版（schema 2）が index.html も変更・バックアップしたと仮定。以前は状態ファイル無しとして片付けを進め、
        // index.html を変更したまま「ゲームは元の状態に戻っています」と伝えていた
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        FakeGame.WriteText(_game, "www/index.html", "<html>patched</html>");
        FakeGame.WriteText(_game, BackupLayout.BackupPathFor("www/index.html"), "<html>original</html>");
        var statePath = Full(BackupLayout.StateFile);
        var state = PatchStateSerializer.Deserialize(File.ReadAllBytes(statePath));
        var newer = state with
        {
            Schema = 2,
            PatchVersion = "0.3.0",
            ModifiedFiles =
            [
                .. state.ModifiedFiles,
                new ModifiedFileRecord("www/index.html", Hashing.Sha256Hex("<html>original</html>"u8), Hashing.Sha256Hex("<html>patched</html>"u8), BackupLayout.BackupPathFor("www/index.html")),
            ],
        };
        File.WriteAllBytes(statePath, PatchStateSerializer.Serialize(newer));
        var before = DirectorySnapshot.Capture(_game);

        var status = Detect();
        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.Equal(PatchStatusKind.NewerVersionApplied, status.Kind);
        Assert.Equal("0.3.0", status.AppliedPatchVersion);
        Assert.False(result.Success);
        Assert.Contains("新しいバージョンのツールで適用されています", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("元の状態に戻っています", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
        Assert.True(PluginsJsEditor.Parse(File.ReadAllText(Full(GameConstants.PluginsJsRelativePath))).Contains(GameConstants.PluginName), "エントリも消していない");
    }

    [Fact]
    public void SameSchemaStateWithFileOutsideCurrentAllowList_RestoreAbortsAndKeepsTheRecord()
    {
        // 新しい版（同じ schema）が www/fonts/SentienceJP.ttf を追加して記録したと仮定。以前は記録ごと消して「元に戻しました」と報告し、
        // 以後どの版でもそのファイルを片付けられなくなっていた
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        FakeGame.WriteText(_game, "www/fonts/SentienceJP.ttf", "newer font");
        var statePath = Full(BackupLayout.StateFile);
        var state = PatchStateSerializer.Deserialize(File.ReadAllBytes(statePath));
        var newer = state with
        {
            PatchVersion = "0.2.0",
            AddedFiles = [.. state.AddedFiles, new AddedFileRecord("www/fonts/SentienceJP.ttf", Hashing.Sha256Hex("newer font"u8))],
        };
        File.WriteAllBytes(statePath, PatchStateSerializer.Serialize(newer));
        var before = DirectorySnapshot.Capture(_game);

        var status = Detect();
        var plan = PatchOperations.PlanFor(status);
        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.Equal(PatchStatusKind.NewerVersionApplied, status.Kind);
        Assert.Contains(status.Reasons, r => r.Contains("www/fonts/SentienceJP.ttf", StringComparison.Ordinal));
        Assert.Null(plan.MainAction);
        Assert.Equal(PatchScreenPlanner.RecheckLabel, plan.MainLabel);
        Assert.False(plan.ShowSecondaryRestore);
        Assert.Contains("v0.2.0", plan.StatusLabel, StringComparison.Ordinal);
        Assert.False(result.Success);
        Assert.Contains("www/fonts/SentienceJP.ttf", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
        Assert.True(File.Exists(statePath), "記録（状態ファイル）は残す");
    }

    [Fact]
    public void NewerVersionApplied_ApplyAndReapplyAbortWithoutAnyChange()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var statePath = Full(BackupLayout.StateFile);
        File.WriteAllText(statePath, "{\"schema\":2,\"patchVersion\":\"0.3.0\",\"newField\":{}}");
        var before = DirectorySnapshot.Capture(_game);

        var applied = PatchApplier.Apply(_game, null, _payload, _log.Add);
        var reapplied = PatchOperations.Execute(PatchAction.Reapply, _game, null, _payload, _log.Add);

        Assert.False(applied.Success);
        Assert.Contains("新しいバージョンのツールで適用されています", applied.Message, StringComparison.Ordinal);
        Assert.False(reapplied.Success);
        Assert.Contains("新しいバージョンのツールで適用されています", reapplied.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void ZeroLengthState_IsStillRecoverableByteForByte()
    {
        // 電源断などで長さ 0 になった状態ファイルは「破損」。従来どおり不整合として、元に戻すでバイト一致に戻る
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        File.WriteAllBytes(Full(BackupLayout.StateFile), []);

        Assert.Equal(PatchStatusKind.Inconsistent, Detect().Kind);
        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void StateWithBom_IsStillRecoverableByteForByte()
    {
        // メモ帳などで BOM 付きで保存された状態ファイルも、従来どおり（破損として）片付けでバイト一致に戻る
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var statePath = Full(BackupLayout.StateFile);
        File.WriteAllBytes(statePath, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(statePath)]);

        var status = Detect();
        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    /// <summary>ゲームフォルダ配下のフルパス。</summary>
    private string Full(string relativePath) => GamePaths.ToFullPath(_game, relativePath);

    /// <summary>現在の状態を判定する。</summary>
    private PatchStatus Detect() => PatchStatusDetector.Detect(GameSnapshotReader.Read(_game, _payload.Manifest), _payload.Manifest);
}
