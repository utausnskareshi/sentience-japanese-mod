using System.Text;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Payload;

/// <summary>
/// 埋め込みペイロード（exe のリソース）の読み込みと、配布前の自己検査のテスト。
/// 実際のアセンブリを作らず、リソース名の一覧とストリームを直接与えて、区切りの正規化や欠落時の動作を確認する。
/// </summary>
public sealed class EmbeddedPayloadSourceTests
{
    [Fact]
    public void Load_WithBackslashResourceNames_NormalizesAndVerifies()
    {
        // ビルド環境によってリソース名の区切りが "\" になっても、"/" に正規化して読めること
        var resources = PayloadResources(separator: '\\');
        var source = CreateSource(resources);

        var bundle = PayloadBundle.Load(source);

        Assert.Equal("0.1.0", bundle.Manifest.PatchVersion);
        Assert.Equal(FakeGame.PayloadFiles.Count, bundle.Files.Count);
        Assert.Equal("// test plugin\n", Encoding.UTF8.GetString(bundle.Files["www/js/plugins/SentienceJP.js"]));
        Assert.True(EmbeddedPayloadSource.HasPayload(resources.Keys));
    }

    [Fact]
    public void HasPayload_WithoutManifest_IsFalse()
    {
        var resources = PayloadResources(separator: '/');
        resources.Remove("payload/manifest.json");

        Assert.False(EmbeddedPayloadSource.HasPayload(resources.Keys));
        Assert.False(EmbeddedPayloadSource.HasPayload(["Other.Resource.txt", "payloads/manifest.json"]));
    }

    [Fact]
    public void ReadGameFile_Missing_ThrowsWithNameAndGuidance()
    {
        var resources = PayloadResources(separator: '/');
        resources.Remove("payload/game/www/sentience_jp/translation.json");
        var source = CreateSource(resources);

        var ex = Assert.Throws<PatchException>(() => PayloadBundle.Load(source));

        Assert.Contains("payload/game/www/sentience_jp/translation.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ダウンロードし直して", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadManifest_StreamCannotBeOpened_Throws()
    {
        var source = new EmbeddedPayloadSource("テスト", ["payload/manifest.json"], _ => null);

        var ex = Assert.Throws<PatchException>(source.ReadManifest);

        Assert.Contains("開けません", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DuplicateAfterNormalization_Throws()
    {
        // "payload/manifest.json" と "payload\manifest.json" はどちらを使うべきか決められないため、黙って片方を使わない
        var ex = Assert.Throws<PatchException>(() =>
            new EmbeddedPayloadSource("テスト", ["payload/manifest.json", "payload\\manifest.json"], _ => new MemoryStream()));

        Assert.Contains("重複", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadGameFile_UnsafePath_Throws()
    {
        var source = CreateSource(PayloadResources(separator: '/'));

        Assert.Throws<PatchException>(() => source.ReadGameFile("../manifest.json"));
    }

    [Fact]
    public void RealAssemblyWithoutPayload_HasNoPayloadAndReportsMissingManifest()
    {
        // 公開コンストラクタ（アセンブリからリソースを読む経路）の確認。テストアセンブリにはペイロードが無い。
        var assembly = typeof(EmbeddedPayloadSourceTests).Assembly;
        var source = new EmbeddedPayloadSource(assembly);

        Assert.False(EmbeddedPayloadSource.HasPayload(assembly));
        Assert.Contains(assembly.GetName().Name!, source.Description, StringComparison.Ordinal);
        var ex = Assert.Throws<PatchException>(source.ReadManifest);
        Assert.Contains("payload/manifest.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfCheck_ValidPayload_Succeeds()
    {
        var result = PayloadSelfCheck.Run(() => CreateSource(PayloadResources(separator: '/')));

        Assert.Equal(PayloadSelfCheck.Success, result.ExitCode);
        Assert.StartsWith("OK", result.Report, StringComparison.Ordinal);
        Assert.Contains("patchVersion=0.1.0", result.Report, StringComparison.Ordinal);
        Assert.Contains("www/sentience_jp/translation.json", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfCheck_ReportsManifestSha256RightAfterVersionLine()
    {
        // AA-01: 合格時の結果に、埋め込んだ manifest.json の生のバイト列の SHA-256 を "manifestSha256=<小文字16進64桁>" の 1 行で出す。
        // 位置は patchVersion の行の直後（ビルドスクリプトと配布 Zip の作成スクリプトとの取り決め）。1 行目とファイル一覧の形式は変えない
        var resources = PayloadResources(separator: '/');
        var expected = Hashing.Sha256Hex(resources["payload/manifest.json"]);

        var result = PayloadSelfCheck.Run(() => CreateSource(resources));
        var lines = result.Report.Split(Environment.NewLine);

        Assert.Equal(PayloadSelfCheck.Success, result.ExitCode);
        Assert.StartsWith("OK: ", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"\ApatchVersion=\S+ translationVersion=\S* files=\d+\z", lines[1]);
        Assert.Equal($"manifestSha256={expected}", lines[2]);
        Assert.Matches(@"\AmanifestSha256=[0-9a-f]{64}\z", lines[2]);
        Assert.All(lines.Skip(3), line => Assert.StartsWith("  ", line, StringComparison.Ordinal));
        Assert.Equal(FakeGame.PayloadFiles.Count, lines.Length - 3);
    }

    [Fact]
    public void SelfCheck_ContentNotMatchingManifest_Fails()
    {
        // dist/payload を手で直して manifest と食い違った状態で exe を作った場合に、配布前に気付けること（A-02）
        var resources = PayloadResources(separator: '/');
        resources["payload/game/www/sentience_jp/translation.json"] = Encoding.UTF8.GetBytes("{\"m:abc\":\"手直し\"}");

        var result = PayloadSelfCheck.Run(() => CreateSource(resources));

        Assert.Equal(PayloadSelfCheck.PayloadInvalid, result.ExitCode);
        Assert.StartsWith("NG", result.Report, StringComparison.Ordinal);
        Assert.Contains("translation.json", result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfCheck_NoEmbeddedPayload_Fails()
    {
        var result = PayloadSelfCheck.Run(() => throw new PatchException("この exe にはペイロードが埋め込まれていません。"));

        Assert.Equal(PayloadSelfCheck.PayloadInvalid, result.ExitCode);
        Assert.Contains("埋め込まれていません", result.Report, StringComparison.Ordinal);
    }

    /// <summary>テスト用のペイロードを、リソース名 → 内容 の辞書として作る。</summary>
    /// <param name="separator">リソース名の区切り文字。</param>
    private static Dictionary<string, byte[]> PayloadResources(char separator)
    {
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"payload{separator}manifest.json"] = Encoding.UTF8.GetBytes(FakeGame.BuildManifestJson("0.1.0", FakeGame.PayloadFiles, [])),
        };
        foreach (var (path, text) in FakeGame.PayloadFiles)
        {
            resources[$"payload{separator}game{separator}{path.Replace('/', separator)}"] = new UTF8Encoding(false).GetBytes(text);
        }

        return resources;
    }

    /// <summary>辞書のリソースを読む取得元を作る。</summary>
    private static EmbeddedPayloadSource CreateSource(IReadOnlyDictionary<string, byte[]> resources) =>
        new("テスト用リソース", resources.Keys, name => resources.TryGetValue(name, out var bytes) ? new MemoryStream(bytes) : null);
}
