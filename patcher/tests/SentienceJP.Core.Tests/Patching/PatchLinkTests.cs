using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// パッチ専用の場所（www/sentience_jp とその配下・sentience_jp_backup とその配下・プラグインファイル）がリンク（ジャンクション）の場合に、
/// 適用・復元が何も変更せずに中止し、リンク先（ゲームフォルダの外）のファイル・空フォルダ・属性に触れないことを確かめる結合テスト（CA-01）。
/// 「ゲームフォルダの外」は、同じ一時フォルダ内の兄弟フォルダ（outside）で模擬し、ゲームフォルダとあわせてスナップショットで比較する。
/// ゲームルート・www・www/js/plugins がジャンクションという正当な構成は、従来どおりバイト一致で往復できることも確かめる。
/// </summary>
public sealed class PatchLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly JunctionScope _junctions = new();
    private readonly string _game;
    private readonly string _outside;
    private readonly PayloadBundle _payload;
    private readonly List<string> _log = [];

    /// <summary>偽のゲーム・外側のフォルダ・ペイロードを用意する。</summary>
    public PatchLinkTests()
    {
        _game = FakeGame.CreateGame(_temp.Combine("game"));
        _outside = _temp.Combine("outside");
        Directory.CreateDirectory(_outside);
        _payload = FakeGame.CreatePayload(_temp.Combine("payload"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // ジャンクションを先に外してから一時フォルダを消す（一時フォルダの削除がリンク先を巻き込まないようにするため）
        _junctions.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void PatchDataFolderJunctionBeforeApply_ApplyFails_AndNeitherOutsideNorGameChanges()
    {
        // 外側（利用者の別フォルダを想定）: 空のフォルダ木と、ファイルのあるフォルダ
        Directory.CreateDirectory(Path.Combine(_outside, "EmptyA", "EmptyB"));
        Directory.CreateDirectory(Path.Combine(_outside, "EmptyC"));
        FakeGame.WriteText(_outside, "Keep/keep.txt", "keep");
        _junctions.Create(Full(GameConstants.PatchDataDirectoryRelativePath), _outside);
        var outsideBefore = DirectorySnapshot.Capture(_outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

        AssertAbortedBecauseOfLink(result, GameConstants.PatchDataDirectoryRelativePath);
        Assert.Empty(DirectorySnapshot.Diff(outsideBefore, DirectorySnapshot.Capture(_outside)));
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
        Assert.True(JunctionScope.IsJunction(Full(GameConstants.PatchDataDirectoryRelativePath)), "利用者のジャンクションは残る");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchDataFolderSwappedToJunctionAfterApply_RestoreFails_AndKeepsOutsideFilesAndEmptyFolders(bool keepState)
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        // 適用後に www/sentience_jp が外へのジャンクションに置き換えられ、外には同名の利用者ファイルと空フォルダがある
        Directory.Delete(Full(GameConstants.PatchDataDirectoryRelativePath), recursive: true);
        FakeGame.WriteText(_outside, "translation.json", "利用者自身の別プロジェクトの翻訳ファイル");
        Directory.CreateDirectory(Path.Combine(_outside, "fonts", "EmptySub"));
        _junctions.Create(Full(GameConstants.PatchDataDirectoryRelativePath), _outside);
        if (!keepState)
        {
            Directory.Delete(Full(GameConstants.BackupDirectoryName), recursive: true);
        }

        var outsideBefore = DirectorySnapshot.Capture(_outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        AssertAbortedBecauseOfLink(result, GameConstants.PatchDataDirectoryRelativePath);
        Assert.True(File.Exists(Path.Combine(_outside, "translation.json")), "外側の利用者ファイルは削除されない");
        Assert.True(Directory.Exists(Path.Combine(_outside, "fonts", "EmptySub")), "外側の空フォルダは削除されない");
        Assert.Empty(DirectorySnapshot.Diff(outsideBefore, DirectorySnapshot.Capture(_outside)));
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void JunctionInsidePreexistingPatchFolder_ApplyFails()
    {
        // www/sentience_jp 自体は普通のフォルダでも、配下のサブフォルダがリンクなら中止する
        Directory.CreateDirectory(Full(GameConstants.PatchDataDirectoryRelativePath));
        _junctions.Create(Full("www/sentience_jp/fonts"), _outside);
        var outsideBefore = DirectorySnapshot.Capture(_outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

        AssertAbortedBecauseOfLink(result, "www/sentience_jp/fonts");
        Assert.Empty(DirectorySnapshot.Diff(outsideBefore, DirectorySnapshot.Capture(_outside)));
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void SubfolderSwappedToJunctionAfterApply_RestoreFails_AndKeepsOutside()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Directory.Delete(Full("www/sentience_jp/fonts"), recursive: true);
        FakeGame.WriteText(_outside, "readme.txt", "外側の同名ファイル");
        Directory.CreateDirectory(Path.Combine(_outside, "EmptySub"));
        _junctions.Create(Full("www/sentience_jp/fonts"), _outside);
        var outsideBefore = DirectorySnapshot.Capture(_outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        AssertAbortedBecauseOfLink(result, "www/sentience_jp/fonts");
        Assert.Empty(DirectorySnapshot.Diff(outsideBefore, DirectorySnapshot.Capture(_outside)));
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void BackupFolderSwappedToJunction_RestoreFails_AndOutsideAttributesStayUnchanged()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);

        // バックアップの中身を外へ移して、元の場所をジャンクションにする。外の状態ファイルには読み取り専用属性を付けておく
        var backup = Full(GameConstants.BackupDirectoryName);
        CopyDirectory(backup, _outside);
        Directory.Delete(backup, recursive: true);
        _junctions.Create(backup, _outside);
        File.SetAttributes(Path.Combine(_outside, GameConstants.StateFileName), FileAttributes.ReadOnly);
        var outsideBefore = DirectorySnapshot.Capture(_outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        AssertAbortedBecauseOfLink(result, GameConstants.BackupDirectoryName);
        Assert.True(File.GetAttributes(Path.Combine(_outside, GameConstants.StateFileName)).HasFlag(FileAttributes.ReadOnly), "外側の読み取り専用属性は外されない");
        Assert.Empty(DirectorySnapshot.Diff(outsideBefore, DirectorySnapshot.Capture(_outside)));
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
        Assert.True(JunctionScope.IsJunction(backup), "ジャンクション自体も削除されない");
    }

    [Fact]
    public void JunctionInsideBackupFolder_RestoreFails_AndKeepsOutside()
    {
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        FakeGame.WriteText(_outside, "precious.txt", "利用者の大事なファイル");
        _junctions.Create(Full("sentience_jp_backup/linked"), _outside);
        var outsideBefore = DirectorySnapshot.Capture(_outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchRestorer.Restore(_game, _payload.Manifest, _log.Add);

        AssertAbortedBecauseOfLink(result, "sentience_jp_backup/linked");
        Assert.Empty(DirectorySnapshot.Diff(outsideBefore, DirectorySnapshot.Capture(_outside)));
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void PluginFileLocationIsJunction_ApplyFails()
    {
        // プラグインファイルの場所（www/js/plugins/SentienceJP.js）がリンク（ここではフォルダへのジャンクション）の場合も中止する
        _junctions.Create(Full(GameConstants.PluginFileRelativePath), _outside);
        var gameBefore = DirectorySnapshot.Capture(_game);

        var result = PatchApplier.Apply(_game, null, _payload, _log.Add);

        AssertAbortedBecauseOfLink(result, GameConstants.PluginFileRelativePath);
        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void GameRootIsJunction_RoundTripIsByteIdentical()
    {
        // ゲームフォルダ自体がジャンクション（別ドライブへ移したゲームなど）は正当な構成として従来どおり扱う（祖先は検査しない）
        var link = _temp.Combine("game_link");
        _junctions.Create(link, _game);
        var before = DirectorySnapshot.Capture(_game);

        Assert.True(PatchApplier.Apply(link, null, _payload, _log.Add).Success);
        Assert.Equal(PatchStatusKind.Applied, PatchStatusDetector.Detect(GameSnapshotReader.Read(link, _payload.Manifest), _payload.Manifest).Kind);
        Assert.True(PatchRestorer.Restore(link, _payload.Manifest, _log.Add).Success);

        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(_game)));
    }

    [Fact]
    public void WwwIsJunction_RoundTripIsByteIdentical()
    {
        var realWww = _temp.Combine("real_www");
        Directory.Move(Full("www"), realWww);
        _junctions.Create(Path.Combine(_game, "www"), realWww);
        var before = DirectorySnapshot.Capture(realWww);

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);

        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(realWww)));
        Assert.True(JunctionScope.IsJunction(Path.Combine(_game, "www")));
    }

    [Fact]
    public void PluginsFolderIsJunction_RoundTripIsByteIdentical()
    {
        var realPlugins = _temp.Combine("real_plugins");
        Directory.Move(Full("www/js/plugins"), realPlugins);
        _junctions.Create(Path.Combine(_game, "www", "js", "plugins"), realPlugins);
        var gameBefore = DirectorySnapshot.Capture(_game);
        var pluginsBefore = DirectorySnapshot.Capture(realPlugins);

        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.True(PatchRestorer.Restore(_game, _payload.Manifest, _log.Add).Success);

        Assert.Empty(DirectorySnapshot.Diff(gameBefore, DirectorySnapshot.Capture(_game)));
        Assert.Empty(DirectorySnapshot.Diff(pluginsBefore, DirectorySnapshot.Capture(realPlugins)));
    }

    [Fact]
    public void FindLinks_ReportsOnlyLinksInPatchOwnedPlaces()
    {
        // パッチ専用の場所の外（www/data）のリンクは対象外。専用の場所の配下とバックアップフォルダのリンクだけを返す
        Directory.CreateDirectory(Full(GameConstants.PatchDataDirectoryRelativePath));
        var dataTarget = _temp.Combine("data_target");
        Directory.CreateDirectory(dataTarget);
        _junctions.Create(Full("www/data_link"), dataTarget);
        _junctions.Create(Full("www/sentience_jp/fonts"), _outside);
        _junctions.Create(Full(GameConstants.BackupDirectoryName), _outside);

        var links = PatchLinkGuard.FindLinks(_game);

        Assert.Equal([GameConstants.BackupDirectoryName, "www/sentience_jp/fonts"], links);
    }

    [Fact]
    public void FindLinks_NormalGame_ReturnsNothing()
    {
        Assert.Empty(PatchLinkGuard.FindLinks(_game));
        Assert.True(PatchApplier.Apply(_game, null, _payload, _log.Add).Success);
        Assert.Empty(PatchLinkGuard.FindLinks(_game));
    }

    [Fact]
    public void ExistingSubdirectories_RootIsJunction_DoesNotEnumerateTarget()
    {
        // 多層防御: 復元の空フォルダ列挙は、ルートがリンクならリンク先を列挙しない
        Directory.CreateDirectory(Path.Combine(_outside, "EmptyA", "EmptyB"));
        _junctions.Create(Full(GameConstants.PatchDataDirectoryRelativePath), _outside);

        Assert.Empty(PatchRestorer.ExistingSubdirectories(_game, GameConstants.PatchOwnedDirectories));
    }

    [Fact]
    public void DeleteDirectoryIfEmpty_Junction_IsNotDeleted()
    {
        // 多層防御: 空のフォルダの削除は、リンク（リンク先が空でも）を削除しない
        var operations = new FileOperations(SharingViolationRetry.None);
        var link = _temp.Combine("link_to_empty");
        _junctions.Create(link, _outside);

        Assert.False(operations.DeleteDirectoryIfEmpty(link));

        Assert.True(JunctionScope.IsJunction(link));
        Assert.True(Directory.Exists(_outside));
    }

    [Fact]
    public void DescribeLinks_NamesLocationsAndTellsToRemoveLinks()
    {
        var message = PatchLinkGuard.DescribeLinks(["sentience_jp_backup", "www/sentience_jp"]);

        Assert.Contains("sentience_jp_backup, www/sentience_jp", message, StringComparison.Ordinal);
        Assert.Contains("何も変更せずに中止しました", message, StringComparison.Ordinal);
        Assert.Contains("リンクを外して", message, StringComparison.Ordinal);
    }

    /// <summary>リンクを理由に、何も変更せずに中止した結果であることを確かめる。</summary>
    private static void AssertAbortedBecauseOfLink(PatchOperationResult result, string expectedLink)
    {
        Assert.False(result.Success);
        Assert.IsType<PatchException>(result.Error);
        Assert.Contains("リンク（ジャンクションまたはシンボリックリンク）", result.Message, StringComparison.Ordinal);
        Assert.Contains(expectedLink, result.Message, StringComparison.Ordinal);
        Assert.Empty(result.CleanupErrors);
    }

    /// <summary>フォルダの中身（ファイル）を別のフォルダへ複製する。</summary>
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>ゲームフォルダ配下のフルパス。</summary>
    private string Full(string relativePath) => GamePaths.ToFullPath(_game, relativePath);
}
