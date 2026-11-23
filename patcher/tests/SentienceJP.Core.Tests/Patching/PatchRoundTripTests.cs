using System.Text;
using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Plugins;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// テンポラリの偽ゲームフォルダで、適用・復元・再適用・更新・ロールバックを通しで検証する結合テスト。
/// 「元に戻す」後は、全ファイルとフォルダが適用前とバイト単位で一致することを確認する。
/// </summary>
public sealed class PatchRoundTripTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _game;
    private readonly PayloadBundle _payload;
    private readonly List<string> _log = [];

    /// <summary>偽のゲームとペイロードを用意する。</summary>
    public PatchRoundTripTests()
    {
        _game = FakeGame.CreateGame(_temp.Combine("game"));
        _payload = FakeGame.CreatePayload(_temp.Combine("payload"));
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ApplyThenRestore_RestoresByteIdenticalTree()
    {
        var before = DirectorySnapshot.Capture(_game);

        var applied = PatchApplier.Apply(_game, "2476493", _payload, _log.Add);
        Assert.True(applied.Success, applied.Message);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);

        var restored = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);
        Assert.True(restored.Success, restored.Message);

        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
        Assert.Equal(PatchStatusKind.NotApplied, Detect().Kind);
    }

    [Fact]
    public void Apply_WritesFilesEntryBackupAndState()
    {
        var originalPlugins = File.ReadAllBytes(PluginsPath);

        var result = PatchApplier.Apply(_game, "2476493", _payload, _log.Add);

        Assert.True(result.Success, result.Message);
        foreach (var (path, text) in FakeGame.PayloadFiles)
        {
            Assert.Equal(text, File.ReadAllText(Full(path)));
        }

        var pluginsText = File.ReadAllText(PluginsPath);
        Assert.Equal(GameConstants.PluginName, PluginsJsEditor.Parse(pluginsText).Elements[^1].Name);
        Assert.Equal(originalPlugins, File.ReadAllBytes(Full("sentience_jp_backup/original/www/js/plugins.js")));

        var state = PatchStateSerializer.Deserialize(File.ReadAllBytes(Full("sentience_jp_backup/state.json")));
        Assert.Equal("0.1.0", state.PatchVersion);
        Assert.Equal("2476493", state.GameBuildId);
        Assert.Equal(FakeGame.PayloadFiles.Keys.Order(StringComparer.Ordinal), state.AddedFiles.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["www/sentience_jp", "www/sentience_jp/fonts"], state.CreatedDirectories);
        var modified = Assert.Single(state.ModifiedFiles);
        Assert.Equal(Hashing.Sha256Hex(originalPlugins), modified.OriginalSha256);
        Assert.Equal(Hashing.Sha256HexOfFile(PluginsPath), modified.PatchedSha256);
    }

    [Fact]
    public void Apply_WhenAlreadyApplied_FailsWithoutChanges()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var afterFirst = DirectorySnapshot.Capture(_game);

        var second = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.False(second.Success);
        Assert.Contains("元に戻す", second.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(afterFirst, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Apply_FailureMidway_RollsBackCompletely()
    {
        var before = DirectorySnapshot.Capture(_game);

        // 他のプログラムが plugins.js を「読み取りのみ共有」で開いたままにして、置き換えの段階（バックアップ・ファイル追加の後）で失敗させる。
        // （読み取り専用属性は、置き換え時に外して付け直すようにしたため、失敗の注入には使えない）
        using (new FileStream(PluginsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Empty(result.CleanupErrors);
            Assert.Contains(_log, line => line.Contains("取り消しています", StringComparison.Ordinal));
        }

        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
        Assert.Equal(PatchStatusKind.NotApplied, Detect().Kind);
    }

    [RealGameFact]
    public void RealGamePluginsJs_ApplyThenRestore_RestoresByteIdenticalTree()
    {
        // 実ゲームの plugins.js を偽ゲームにコピーして、両方の復元経路（バックアップ・エントリの除去）で完全に元通りになることを確かめる
        // （実ゲームのファイルは読み取りのみ。書き込みはテンポラリの偽ゲームだけに行う）
        File.WriteAllBytes(PluginsPath, File.ReadAllBytes(RealGameFactAttribute.RealPluginsJsPath));
        var before = DirectorySnapshot.Capture(_game);

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Directory.Delete(Full(GameConstants.BackupDirectoryName), recursive: true);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void SteamRevertedPluginsJs_NeedsReapply_AndReapplyThenRestoreWorks()
    {
        var original = File.ReadAllBytes(PluginsPath);
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        // Steam の整合性チェックで plugins.js だけ元に戻された状況を再現する
        File.WriteAllBytes(PluginsPath, original);
        Assert.Equal(PatchStatusKind.NeedsReapply, Detect().Kind);

        var reapplied = PatchOperations.Execute(PatchAction.Reapply, _game, null, _payload, _log.Add);
        Assert.True(reapplied.Success, reapplied.Message);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);

        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Restore_AfterPluginsJsChangedByOthers_RemovesOnlyOwnEntry()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        // 適用後に別の MOD が plugins.js へ要素を追加した状況（バックアップで戻すと他者の変更が消えるため、エントリ除去で戻す）
        var other = "{\"name\":\"OtherMod\",\"status\":true,\"description\":\"\",\"parameters\":{}}";
        var withOther = PluginsJsEditor.InsertEntry(File.ReadAllText(PluginsPath), other);
        File.WriteAllText(PluginsPath, withOther, new UTF8Encoding(false));

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        var names = PluginsJsEditor.Parse(File.ReadAllText(PluginsPath)).Elements.Select(e => e.Name).ToList();
        Assert.Equal(["PluginA", "PluginB", "PluginC", "OtherMod"], names);
        Assert.Equal(PluginsJsEditor.InsertEntry(FakeGame.SamplePluginsJs, other), File.ReadAllText(PluginsPath));
    }

    [Fact]
    public void Outdated_UpdateAppliesNewVersion_ThenRestoreWorks()
    {
        var before = DirectorySnapshot.Capture(_game);
        var oldPayload = FakeGame.CreatePayload(_temp.Combine("old_payload"), patchVersion: "0.0.9");
        Assert.True(PatchApplier.Apply(_game, null, oldPayload, _log.Add).Success);
        Assert.Equal(PatchStatusKind.Outdated, Detect().Kind);

        var updated = PatchOperations.Execute(PatchAction.Update, _game, null, _payload, _log.Add);

        Assert.True(updated.Success, updated.Message);
        var status = Detect();
        Assert.Equal(PatchStatusKind.Applied, status.Kind);
        Assert.Equal("0.1.0", status.AppliedPatchVersion);

        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Inconsistent_LeftoversWithoutState_RestoreCleansUp()
    {
        var before = DirectorySnapshot.Capture(_game);

        // 状態ファイルが無いのに、エントリとファイルが残っている状況
        FakeGame.WriteText(_game, "www/sentience_jp/translation.json", "{}");
        FakeGame.WriteText(_game, "www/js/plugins/SentienceJP.js", "// old");
        File.WriteAllText(PluginsPath, PluginsJsEditor.InsertEntry(FakeGame.SamplePluginsJs, FakeGame.PluginEntryJson), new UTF8Encoding(false));
        Assert.Equal(PatchStatusKind.Inconsistent, Detect().Kind);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
        Assert.Equal(PatchStatusKind.NotApplied, Detect().Kind);
    }

    [Fact]
    public void Restore_WithoutBackupFolder_RemovesEntryTextually()
    {
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        // 利用者がバックアップフォルダ（状態ファイルを含む）を消してしまった状況
        Directory.Delete(Full("sentience_jp_backup"), recursive: true);
        Assert.Equal(PatchStatusKind.Inconsistent, Detect().Kind);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Restore_IsIdempotent()
    {
        var before = DirectorySnapshot.Capture(_game);
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);

        var second = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(second.Success, second.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Restore_KeepsUnknownFilesInPatchFolder()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        // パッチのフォルダに利用者が別のファイルを置いた場合、そのファイルとフォルダは消さない
        FakeGame.WriteText(_game, "www/sentience_jp/user_note.txt", "keep me");

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Full("www/sentience_jp/user_note.txt")));
        Assert.False(File.Exists(Full("www/sentience_jp/translation.json")));
        Assert.False(Directory.Exists(Full("www/sentience_jp/fonts")));
    }

    [Fact]
    public void Apply_PreservesCrlfAndBomOfPluginsJs()
    {
        var crlfWithBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(new UTF8Encoding(false).GetBytes(FakeGame.SamplePluginsJs.Replace("\n", "\r\n", StringComparison.Ordinal)))
            .ToArray();
        File.WriteAllBytes(PluginsPath, crlfWithBom);
        var before = DirectorySnapshot.Capture(_game);

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        var patched = File.ReadAllBytes(PluginsPath);
        Assert.True(patched.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains(",\r\n{\"name\":\"SentienceJP\"", Encoding.UTF8.GetString(patched), StringComparison.Ordinal);

        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Theory]
    [InlineData("// Generated by RPG Maker.\n// var $plugins = [] is overwritten by the editor. Do not edit.\nvar $plugins =\n[\n{\"name\":\"PluginA\",\"status\":true,\"description\":\"\",\"parameters\":{}}\n];\n")]
    [InlineData("var note = 'var $plugins = [] is generated';\nvar $plugins =\n[\n{\"name\":\"PluginA\",\"status\":true,\"description\":\"\",\"parameters\":{}}\n];\n")]
    [InlineData("/* var $plugins = [ */\nvar $plugins =\n[\n{\"name\":\"PluginA\",\"status\":true,\"description\":\"\",\"parameters\":{}}\n];\n")]
    public void Apply_MarkerInLeadingCommentOrString_InsertsIntoRealArray_AndRestoreIsByteIdentical(string pluginsJs)
    {
        // CA-06: 宣言より前のコメント・文字列にある "var $plugins = [" へは挿入せず、本物の配列に登録する
        // （以前はコメントの中へ挿入して JavaScript の構文エラーにしていたのに「適用済み」と判定していた）
        File.WriteAllText(PluginsPath, pluginsJs, new UTF8Encoding(false));
        var before = DirectorySnapshot.Capture(_game);
        var realDeclaration = pluginsJs.LastIndexOf("var $plugins =", StringComparison.Ordinal);

        var applied = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.True(applied.Success, applied.Message);
        Assert.Equal(PatchStatusKind.Applied, Detect().Kind);
        var patched = File.ReadAllText(PluginsPath);
        Assert.Equal(pluginsJs[..realDeclaration], patched[..realDeclaration]);
        Assert.Contains("\"name\":\"SentienceJP\"", patched[realDeclaration..], StringComparison.Ordinal);
        Assert.Equal(["PluginA", GameConstants.PluginName], PluginsJsEditor.Parse(patched).Elements.Select(e => e.Name));

        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void Apply_TwoCodeDeclarations_RefusesWithoutChanges()
    {
        // CA-06: コードとしての宣言が 2 つあれば、どちらがプラグインの一覧か判断できないため適用しない（何も書き換えない）
        File.WriteAllText(PluginsPath, "var $plugins = [];\nvar $plugins = [];\n", new UTF8Encoding(false));
        var before = DirectorySnapshot.Capture(_game);

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

        Assert.False(result.Success);
        Assert.Contains("2 個", result.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    /// <summary>plugins.js のフルパス。</summary>
    private string PluginsPath => Full(GameConstants.PluginsJsRelativePath);

    /// <summary>ゲームフォルダ配下のフルパス。</summary>
    private string Full(string relativePath) => GamePaths.ToFullPath(_game, relativePath);

    /// <summary>現在の状態を判定する。</summary>
    private PatchStatus Detect() => PatchStatusDetector.Detect(GameSnapshotReader.Read(_game, _payload.Manifest), _payload.Manifest);
}
