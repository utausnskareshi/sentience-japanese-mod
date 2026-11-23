using System.Text;
using SentienceJP.Core.Payload;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Payload;

/// <summary>ペイロード（manifest.json と追加ファイル）の解析・検証のテスト。</summary>
public sealed class PayloadTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Load_ValidPayload_ReturnsVerifiedBundle()
    {
        var bundle = FakeGame.CreatePayload(_temp.FullPath, knownPluginsJsSha256: [new string('a', 64)]);

        Assert.Equal("0.1.0", bundle.Manifest.PatchVersion);
        Assert.Equal(GameConstants.PluginName, bundle.Manifest.PluginName);
        Assert.Equal(3, bundle.Files.Count);
        Assert.Equal("// test plugin\n", Encoding.UTF8.GetString(bundle.Files["www/js/plugins/SentienceJP.js"]));
        Assert.Equal([new string('a', 64)], bundle.Manifest.KnownPluginsJsSha256);
        Assert.Equal(["2476493"], bundle.Manifest.KnownBuildIds);
    }

    [Fact]
    public void PluginEntryJson_IsCompactAndAsciiSafe()
    {
        var bundle = FakeGame.CreatePayload(_temp.FullPath);

        // 古い JavaScript エンジンでも安全に解釈できるよう、非 ASCII 文字はエスケープされ、改行を含まない
        Assert.DoesNotContain('\n', bundle.Manifest.PluginEntryJson);
        Assert.True(bundle.Manifest.PluginEntryJson.All(c => c < 0x80));
        Assert.StartsWith("{\"name\":\"SentienceJP\",\"status\":true", bundle.Manifest.PluginEntryJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ContentMismatch_Throws()
    {
        FakeGame.CreatePayload(_temp.FullPath);
        FakeGame.WriteText(Path.Combine(_temp.FullPath, "game"), "www/sentience_jp/translation.json", "{\"m:abc\":\"改ざん\"}");

        var ex = Assert.Throws<PatchException>(() => PayloadBundle.Load(new DirectoryPayloadSource(_temp.FullPath)));
        Assert.Contains("translation.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        FakeGame.CreatePayload(_temp.FullPath);
        File.Delete(_temp.Combine("game/www/js/plugins/SentienceJP.js"));

        var ex = Assert.Throws<PatchException>(() => PayloadBundle.Load(new DirectoryPayloadSource(_temp.FullPath)));
        Assert.Contains("SentienceJP.js", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("www/../evil.js", "不正なパス")]
    [InlineData("www/js/plugins.js", "上書きしてはいけない")]
    [InlineData("nw.exe", "上書きしてはいけない")]
    [InlineData("sentience_jp_backup/state.json", "上書きしてはいけない")]
    public void Parse_UnsafeFilePath_Throws(string path, string expectedFragment)
    {
        var json = FakeGame.BuildManifestJson("0.1.0", new Dictionary<string, string> { [path] = "x" }, []);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DuplicatePaths_Throws()
    {
        // 大文字小文字だけが違うパスは Windows では同じファイルを指すため重複とみなす
        // （パスの許可リスト導入に合わせ、許可された場所 www/sentience_jp/ 配下のパスで確認する）
        var json = FakeGame.BuildManifestJson("0.1.0", new Dictionary<string, string> { ["www/sentience_jp/a.json"] = "x" }, [])
            .Replace("\"files\":[", "\"files\":[{\"path\":\"WWW/SENTIENCE_JP/A.json\",\"sha256\":\"" + new string('0', 64) + "\",\"size\":1},", StringComparison.Ordinal);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("重複", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("www/js/rpg_core.js")]
    [InlineData("www/js/plugins/Other.js")]
    [InlineData("www/index.html")]
    [InlineData("www/sentience_jp")]
    [InlineData("www/sentience_jpx/a.json")]
    [InlineData("save/file1.rpgsave")]
    public void Parse_PathOutsideAllowList_Throws(string path)
    {
        // 既存のゲームファイル等を manifest に書かれても受け付けない（素のゲームを不整合と誤判定し、片付けで削除するのを防ぐ。C-03）
        var json = FakeGame.BuildManifestJson("0.1.0", new Dictionary<string, string> { [path] = "x" }, []);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("追加できない場所", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("www/js/plugins.js.")]
    [InlineData("www/js/plugins.js ")]
    [InlineData("www/sentience_jp/a.txt.")]
    [InlineData("www/sentience_jp/a.txt ")]
    [InlineData("www/sentience_jp/CON")]
    [InlineData("www/sentience_jp/nul.txt")]
    [InlineData("www/sentience_jp/SENTIE~1.JSO")]
    [InlineData("sentience_jp_backup./state.json")]
    public void Parse_WindowsAliasPath_Throws(string path)
    {
        // Windows が別のファイルとして解釈する名前（末尾の "."・空白、予約デバイス名、8.3 形式の短い名前）は拒否する（C-03）
        var json = FakeGame.BuildManifestJson("0.1.0", new Dictionary<string, string> { [path] = "x" }, []);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("不正なパス", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AliasDuplicateOfAllowedPath_Throws()
    {
        // 「a.txt」と「a.txt.」のように別名で同じファイルを指す組み合わせも、別名の側で拒否される
        var json = FakeGame.BuildManifestJson(
            "0.1.0",
            new Dictionary<string, string> { ["www/sentience_jp/a.txt"] = "1", ["www/sentience_jp/a.txt."] = "2" },
            []);

        Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("\"schema\":1", "\"schema\":\"1\"", "schema")]
    [InlineData("\"schema\":1", "\"schema\":null", "schema")]
    [InlineData("\"schema\":1", "\"schema\":1.5", "schema")]
    [InlineData("\"schema\":1", "\"schema\":true", "schema")]
    [InlineData("\"size\":15", "\"size\":\"15\"", "size")]
    [InlineData("\"size\":15", "\"size\":null", "size")]
    [InlineData("\"size\":15", "\"size\":1.5", "size")]
    [InlineData("\"size\":15", "\"size\":-1", "size")]
    public void Parse_WrongValueTypes_ThrowsPatchException(string from, string to, string expectedFragment)
    {
        // 値の型違いは InvalidOperationException ではなく、説明つきの PatchException にする（C-08）
        var json = FakeGame.BuildManifestJson("0.1.0", FakeGame.PayloadFiles, []);
        Assert.Contains(from, json, StringComparison.Ordinal);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json.Replace(from, to, StringComparison.Ordinal))));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"schema\":1", "\"schema\":2", "未対応")]
    [InlineData("\"name\":\"SentienceJP\"", "\"name\":\"Other\"", "pluginEntry.name")]
    [InlineData("\"status\":true", "\"status\":false", "status")]
    [InlineData("\"knownPluginsJsSha256\":[]", "\"knownPluginsJsSha256\":[\"xyz\"]", "knownPluginsJsSha256")]
    [InlineData("\"patchVersion\":\"0.1.0\"", "\"patchVersion\":\"\"", "patchVersion")]
    public void Parse_InvalidFields_Throws(string from, string to, string expectedFragment)
    {
        var json = FakeGame.BuildManifestJson("0.1.0", FakeGame.PayloadFiles, []).Replace(from, to, StringComparison.Ordinal);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.2.3-beta.1")]
    [InlineData("1.0.0+abc.def")]
    [InlineData("1.2.3.4")]
    [InlineData("10")]
    public void Parse_ValidPatchVersion_IsAccepted(string version)
    {
        // AA-15: ビルドスクリプトと同じ形式の版数は受け付ける（現行の "0.1.0" を含む）
        var json = FakeGame.BuildManifestJson(version, FakeGame.PayloadFiles, []);

        Assert.Equal(version, PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)).PatchVersion);
    }

    [Theory]
    [InlineData("0.1.0\\n")]
    [InlineData("0.1.0 ")]
    [InlineData(" 0.1.0")]
    [InlineData("v0.1.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData("１.０.０")]
    [InlineData("١.٢.٣")]
    [InlineData("1..0")]
    [InlineData("1.0-")]
    public void Parse_MalformedPatchVersion_Throws(string version)
    {
        // AA-15: 末尾の改行・空白・接頭辞・5 個以上の数・全角数字・アラビア数字などは拒否する（\d や $ のすり抜けを防ぐ）
        var json = FakeGame.BuildManifestJson(version, FakeGame.PayloadFiles, []);

        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("patchVersion", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.1.0\n", false)]
    [InlineData("0.1.0\r\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PatchVersionFormat_IsValid_MatchesBuildScriptRule(string? version, bool expected)
    {
        Assert.Equal(expected, PatchVersionFormat.IsValid(version));
    }

    [Fact]
    public void Parse_NotJson_Throws()
    {
        var ex = Assert.Throws<PatchException>(() => PayloadManifestParser.Parse("not json"u8));
        Assert.Contains("JSON", ex.Message, StringComparison.Ordinal);
    }
}
