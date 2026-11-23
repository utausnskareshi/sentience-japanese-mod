using System.Text;
using SentienceJP.Core.Patching;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// state.json の読み込み（<see cref="PatchStateSerializer.Deserialize"/>）の異常系のテスト。
/// 壊れた・改ざんされた状態ファイルは、例外の種類を問わず <see cref="PatchException"/> として報告され、
/// 状態判定では「状態ファイルのエラー」として扱われる（画面が起動できなくなったり、ゲームのファイルを消したりしない）。
/// </summary>
public sealed class PatchStateSerializerTests : IDisposable
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly string HashC = new('c', 64);

    private static readonly string DefaultAdded =
        $"[{{\"path\":\"www/js/plugins/SentienceJP.js\",\"sha256\":\"{HashA}\"}}]";

    private static readonly string DefaultModified =
        $"[{{\"path\":\"www/js/plugins.js\",\"originalSha256\":\"{HashB}\",\"patchedSha256\":\"{HashC}\",\"backupPath\":\"sentience_jp_backup/original/www/js/plugins.js\"}}]";

    private const string DefaultCreated = "[\"www/sentience_jp\"]";

    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Deserialize_Valid_ReadsAllFields()
    {
        var state = PatchStateSerializer.Deserialize(Bytes(StateJson()));

        Assert.Equal("0.1.0", state.PatchVersion);
        Assert.Equal("www/js/plugins/SentienceJP.js", Assert.Single(state.AddedFiles).Path);
        Assert.Equal(HashC, Assert.Single(state.ModifiedFiles).PatchedSha256);
        Assert.Equal(["www/sentience_jp"], state.CreatedDirectories);
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTrips()
    {
        var original = PatchStateSerializer.Deserialize(Bytes(StateJson()));

        var again = PatchStateSerializer.Deserialize(PatchStateSerializer.Serialize(original));

        Assert.Equal(original.AppliedAt, again.AppliedAt);
        Assert.Equal(original.AddedFiles, again.AddedFiles);
        Assert.Equal(original.ModifiedFiles, again.ModifiedFiles);
        Assert.Equal(original.CreatedDirectories, again.CreatedDirectories);
    }

    [Theory]
    [InlineData("not json", "壊れています")]
    [InlineData("null", "空です")]
    [InlineData("[]", "壊れています")]
    [InlineData("{}", "壊れています")]
    public void Deserialize_BrokenJson_ThrowsPatchException(string json, string expectedFragment)
    {
        var ex = Assert.Throws<PatchException>(() => PatchStateSerializer.Deserialize(Bytes(json)));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("addedFiles", "[null]")]
    [InlineData("modifiedFiles", "[null]")]
    [InlineData("createdDirectories", "[null]")]
    [InlineData("addedFiles", "[{\"path\":null,\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]")]
    [InlineData("addedFiles", "null")]
    [InlineData("schema", "\"1\"")]
    [InlineData("schema", "2")]
    public void Deserialize_NullOrWrongTypedValues_ThrowsPatchException(string property, string value)
    {
        // 配列の要素の null は自動の null 検査の対象外のため、明示的に検査して PatchException にする（C-07）
        var json = property switch
        {
            "addedFiles" => StateJson(addedFiles: value),
            "modifiedFiles" => StateJson(modifiedFiles: value),
            "createdDirectories" => StateJson(createdDirectories: value),
            _ => StateJson(schema: value),
        };

        Assert.Throws<PatchException>(() => PatchStateSerializer.Deserialize(Bytes(json)));
    }

    [Theory]
    [InlineData("www/js/rpg_core.js")]
    [InlineData("www/js/plugins.js")]
    [InlineData("www/sentience_jp/a.json.")]
    [InlineData("../outside.txt")]
    [InlineData("sentience_jp_backup/original/www/js/plugins.js")]
    public void Deserialize_AddedFileOutsideAllowList_Throws(string path)
    {
        // 改ざん・破損した記録で、ゲームのファイルやバックアップを「追加ファイル」として削除しないため（C-03）
        var json = StateJson(addedFiles: $"[{{\"path\":\"{path}\",\"sha256\":\"{HashA}\"}}]");

        var ex = Assert.Throws<PatchException>(() => PatchStateSerializer.Deserialize(Bytes(json)));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("www/data")]
    [InlineData("sentience_jp_backup")]
    [InlineData("www/sentience_jp ")]
    public void Deserialize_CreatedDirectoryOutsideAllowList_Throws(string path)
    {
        var json = StateJson(createdDirectories: $"[\"{path}\"]");

        Assert.Throws<PatchException>(() => PatchStateSerializer.Deserialize(Bytes(json)));
    }

    [Theory]
    [InlineData("sentience_jp_backup/original/www/js/other.js")]
    [InlineData("www/js/plugins.js")]
    [InlineData("../plugins.js")]
    public void Deserialize_UnexpectedBackupPath_Throws(string backupPath)
    {
        var json = StateJson(modifiedFiles:
            $"[{{\"path\":\"www/js/plugins.js\",\"originalSha256\":\"{HashB}\",\"patchedSha256\":\"{HashC}\",\"backupPath\":\"{backupPath}\"}}]");

        Assert.Throws<PatchException>(() => PatchStateSerializer.Deserialize(Bytes(json)));
    }

    [Fact]
    public void Deserialize_InvalidHash_Throws()
    {
        var json = StateJson(addedFiles: "[{\"path\":\"www/js/plugins/SentienceJP.js\",\"sha256\":\"XYZ\"}]");

        var ex = Assert.Throws<PatchException>(() => PatchStateSerializer.Deserialize(Bytes(json)));

        Assert.Contains("sha256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_ModifiedFileOtherThanPluginsJs_IsReadable()
    {
        // 将来の版が plugins.js 以外の変更を記録した場合も読み込みはでき、復元側で「未対応」として安全に中止する（C-11）
        var json = StateJson(modifiedFiles:
            $"[{{\"path\":\"www/index.html\",\"originalSha256\":\"{HashB}\",\"patchedSha256\":\"{HashC}\",\"backupPath\":\"sentience_jp_backup/original/www/index.html\"}}]");

        var state = PatchStateSerializer.Deserialize(Bytes(json));

        Assert.Equal("www/index.html", Assert.Single(state.ModifiedFiles).Path);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"path\":\"../www/js/rpg_core.js\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]")]
    public void BrokenStateInGame_IsReportedAsStateError_AndRestoreStillWorks(string addedFiles)
    {
        // 状態ファイルが壊れていても状態確認で例外にならず（不整合として表示）、状態ファイル無しの復元で元通りにできる（C-07・E3）
        var game = FakeGame.CreateGame(_temp.Combine("game"));
        var payload = FakeGame.CreatePayload(_temp.Combine("payload"));
        var before = DirectorySnapshot.Capture(game);
        Assert.True(PatchApplier.Apply(game, null, payload, _ => { }).Success);
        File.WriteAllText(GamePaths.ToFullPath(game, BackupLayout.StateFile), StateJson(addedFiles: addedFiles), new UTF8Encoding(false));

        var snapshot = GameSnapshotReader.Read(game, payload.Manifest);
        Assert.Null(snapshot.State);
        Assert.NotNull(snapshot.StateError);
        Assert.Equal(PatchStatusKind.Inconsistent, PatchStatusDetector.Detect(snapshot, payload.Manifest).Kind);

        var restored = PatchRestorer.Restore(game, payload.Manifest, _ => { });

        Assert.True(restored.Success, restored.Message);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(game)));
    }

    [Fact]
    public void Read_Valid_IsValid()
    {
        var result = PatchStateSerializer.Read(Bytes(StateJson()));

        Assert.True(result.IsValid);
        Assert.NotNull(result.State);
        Assert.Null(result.CorruptReason);
        Assert.Null(result.Unsupported);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"2\"}")]
    public void Read_BrokenJson_IsCorrupt(string json)
    {
        // CA-04: JSON 不正・長さ 0・型違い・null は「破損」（従来どおり、状態ファイル無しとして片付ける）
        var result = PatchStateSerializer.Read(Bytes(json));

        Assert.False(result.IsValid);
        Assert.Null(result.State);
        Assert.NotNull(result.CorruptReason);
        Assert.Null(result.Unsupported);
    }

    [Fact]
    public void Read_WithBom_IsCorrupt_AsBefore()
    {
        // CA-04: BOM 付き（メモ帳で保存した等）は従来どおり破損として扱い、片付けで回復させる
        var result = PatchStateSerializer.Read([0xEF, 0xBB, 0xBF, .. Bytes(StateJson())]);

        Assert.NotNull(result.CorruptReason);
        Assert.Null(result.Unsupported);
    }

    [Theory]
    [InlineData("addedFiles", "[null]")]
    [InlineData("addedFiles", "[{\"path\":\"www/sentience_jp/a.json\",\"sha256\":\"XYZ\"}]")]
    [InlineData("addedFiles", "[{\"path\":\"../outside.txt\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]")]
    [InlineData("addedFiles", "[{\"path\":\"C:/Windows/win.ini\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]")]
    [InlineData("addedFiles", "[{\"path\":\"/www/sentience_jp/a.json\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]")]
    [InlineData("addedFiles", "[{\"path\":\"www/sentience_jp/a.json.\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]")]
    [InlineData("modifiedFiles", "[{\"path\":\"www/js/plugins.js\",\"originalSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"patchedSha256\":\"c\",\"backupPath\":\"sentience_jp_backup/original/www/js/plugins.js\"}]")]
    [InlineData("modifiedFiles", "[{\"path\":\"www/js/plugins.js\",\"originalSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"patchedSha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"backupPath\":\"../plugins.js\"}]")]
    [InlineData("createdDirectories", "[\"www/sentience_jp/..\"]")]
    [InlineData("schema", "0")]
    public void Read_CorruptContent_IsCorrupt(string property, string value)
    {
        // CA-04: null・不正なハッシュ・構文として不正なパス（..・絶対パス・Windows の別名）は「破損」
        var json = property switch
        {
            "addedFiles" => StateJson(addedFiles: value),
            "modifiedFiles" => StateJson(modifiedFiles: value),
            "createdDirectories" => StateJson(createdDirectories: value),
            _ => StateJson(schema: value),
        };

        var result = PatchStateSerializer.Read(Bytes(json));

        Assert.NotNull(result.CorruptReason);
        Assert.Null(result.Unsupported);
    }

    [Theory]
    [InlineData("{\"schema\":2,\"patchVersion\":\"0.3.0\",\"somethingNew\":[1,2,3]}", "0.3.0")]
    [InlineData("{\"schema\":2}", null)]
    [InlineData("{\"schema\":3,\"patchVersion\":\"0.3.0\\n\"}", null)]
    public void Read_NewerSchema_IsUnsupported_EvenIfShapeDiffers(string json, string? expectedVersion)
    {
        // CA-04: 形式（schema）が新しければ、内容の形が違っていても「未対応」（新しいバージョンのツールで適用された）
        var result = PatchStateSerializer.Read(Bytes(json));

        Assert.False(result.IsValid);
        Assert.Null(result.CorruptReason);
        var unsupported = Assert.IsType<UnsupportedPatchState>(result.Unsupported);
        Assert.Equal(expectedVersion, unsupported.PatchVersion);
        Assert.Empty(unsupported.Entries);
        Assert.Contains("schema=", unsupported.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("addedFiles", "[{\"path\":\"www/fonts/SentienceJP.ttf\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]", "www/fonts/SentienceJP.ttf")]
    [InlineData("addedFiles", "[{\"path\":\"www/js/rpg_core.js\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]", "www/js/rpg_core.js")]
    [InlineData("createdDirectories", "[\"www/fonts\"]", "www/fonts")]
    [InlineData("modifiedFiles", "[{\"path\":\"www/index.html\",\"originalSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"patchedSha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"backupPath\":\"sentience_jp_backup/original/www/index.html\"}]", "www/index.html")]
    [InlineData("modifiedFiles", "[{\"path\":\"www/js/plugins.js\",\"originalSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"patchedSha256\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"backupPath\":\"sentience_jp_backup/v2/www/js/plugins.js\"}]", "sentience_jp_backup/v2/www/js/plugins.js")]
    public void Read_ValidSyntaxOutsideAllowList_IsUnsupported(string property, string value, string expectedEntry)
    {
        // CA-04: 構文は正しいが現行の許可リスト外の記録は「未対応」（新しいバージョンのツールが残したもの）
        var json = property switch
        {
            "addedFiles" => StateJson(addedFiles: value),
            "modifiedFiles" => StateJson(modifiedFiles: value),
            _ => StateJson(createdDirectories: value),
        };

        var result = PatchStateSerializer.Read(Bytes(json));

        Assert.Null(result.CorruptReason);
        var unsupported = Assert.IsType<UnsupportedPatchState>(result.Unsupported);
        Assert.Contains(expectedEntry, unsupported.Entries);
        Assert.Contains(expectedEntry, unsupported.Summary, StringComparison.Ordinal);
        Assert.Equal("0.1.0", unsupported.PatchVersion);
    }

    [Fact]
    public void StateWithAllowListOutsidePathInGame_IsNewerVersion_AndRestoreChangesNothing()
    {
        // CA-04: 構文は正しいが許可リスト外の記録（以前は「破損」扱いで片付けていた入力）は、新しいバージョンで適用されたものとして
        // 状態ファイルの記録を残したまま、何も変更せずに中止する（扱えない記録を消して、以後どの版でも片付けられなくなるのを防ぐ）
        var game = FakeGame.CreateGame(_temp.Combine("game"));
        var payload = FakeGame.CreatePayload(_temp.Combine("payload"));
        Assert.True(PatchApplier.Apply(game, null, payload, _ => { }).Success);
        File.WriteAllText(
            GamePaths.ToFullPath(game, BackupLayout.StateFile),
            StateJson(addedFiles: $"[{{\"path\":\"www/js/rpg_core.js\",\"sha256\":\"{HashA}\"}}]"),
            new UTF8Encoding(false));
        var before = DirectorySnapshot.Capture(game);

        var snapshot = GameSnapshotReader.Read(game, payload.Manifest);
        Assert.Null(snapshot.State);
        Assert.Null(snapshot.StateError);
        Assert.NotNull(snapshot.UnsupportedState);
        Assert.Equal(PatchStatusKind.NewerVersionApplied, PatchStatusDetector.Detect(snapshot, payload.Manifest).Kind);

        var restored = PatchRestorer.Restore(game, payload.Manifest, _ => { });

        Assert.False(restored.Success);
        Assert.Contains("www/js/rpg_core.js", restored.Message, StringComparison.Ordinal);
        Assert.Empty(DirectorySnapshot.Diff(before, DirectorySnapshot.Capture(game)));
    }

    /// <summary>テスト用の state.json の内容を、項目を差し替えて作る。</summary>
    private static string StateJson(
        string? addedFiles = null,
        string? modifiedFiles = null,
        string createdDirectories = DefaultCreated,
        string schema = "1") =>
        "{" +
        $"\"schema\":{schema}," +
        "\"patchVersion\":\"0.1.0\"," +
        "\"translationVersion\":\"t\"," +
        "\"appliedAt\":\"2026-11-23T00:00:00+00:00\"," +
        "\"gameBuildId\":null," +
        $"\"addedFiles\":{addedFiles ?? DefaultAdded}," +
        $"\"modifiedFiles\":{modifiedFiles ?? DefaultModified}," +
        $"\"createdDirectories\":{createdDirectories}" +
        "}";

    /// <summary>UTF-8（BOM なし）のバイト列にする。</summary>
    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);
}
