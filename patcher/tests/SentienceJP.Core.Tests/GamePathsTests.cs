using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests;

/// <summary>相対パスの検証・変換のテスト（パストラバーサル対策）。</summary>
public sealed class GamePathsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/www/a.js")]
    [InlineData("www\\a.js")]
    [InlineData("../outside.txt")]
    [InlineData("www/../../outside.txt")]
    [InlineData("www/./a.js")]
    [InlineData("C:/Windows/a.dll")]
    [InlineData("www//a.js")]
    [InlineData("www/a?.js")]
    public void ValidateRelativePath_RejectsUnsafePaths(string path)
    {
        Assert.Throws<PatchException>(() => GamePaths.ValidateRelativePath(path));
    }

    [Theory]
    [InlineData("www/js/plugins/SentienceJP.js")]
    [InlineData("www/sentience_jp/translation.json")]
    [InlineData("nw.exe")]
    public void ValidateRelativePath_AcceptsNormalPaths(string path)
    {
        GamePaths.ValidateRelativePath(path);
    }

    [Fact]
    public void ToFullPath_StaysUnderGameDirectory()
    {
        using var temp = new TempDirectory();

        var full = GamePaths.ToFullPath(temp.FullPath, "www/js/plugins.js");

        Assert.True(GamePaths.IsUnderDirectory(full, temp.FullPath));
        Assert.EndsWith(Path.Combine("www", "js", "plugins.js"), full, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(temp.FullPath), GamePaths.ToFullPath(temp.FullPath, string.Empty));
    }

    [Fact]
    public void IsUnderDirectory_DoesNotConfuseSiblingPrefix()
    {
        // "C:\Game2\x" は "C:\Game" の配下ではない（前方一致の誤判定を防ぐ）
        Assert.False(GamePaths.IsUnderDirectory(@"C:\Game2\x.txt", @"C:\Game"));
        Assert.True(GamePaths.IsUnderDirectory(@"C:\Game\x.txt", @"C:\game\"));
    }

    [Theory]
    [InlineData(@"C:\www\js\plugins.js", @"C:\", true)]
    [InlineData(@"C:\www", @"C:\", true)]
    [InlineData(@"D:\www", @"C:\", false)]
    [InlineData(@"\\server\share\www\x.js", @"\\server\share", true)]
    [InlineData(@"\\server\share\www\x.js", @"\\server\share\", true)]
    [InlineData(@"\\server\share2\x.js", @"\\server\share", false)]
    [InlineData(@"C:\Game\x.js", @"C:\Game", true)]
    [InlineData(@"C:\Game\x.js", @"C:\Game\", true)]
    [InlineData(@"C:\Game", @"C:\Game", false)]
    [InlineData(@"C:\GameX\x.js", @"C:\Game", false)]
    public void IsUnderDirectory_HandlesDriveRootUncRootAndNormalFolders(string path, string directory, bool expected)
    {
        // CA-14: ドライブ直下（"C:\"）や UNC 共有のルートでも区切りを二重にせず、正しく判定する
        Assert.Equal(expected, GamePaths.IsUnderDirectory(path, directory));
    }

    [Fact]
    public void ToFullPath_DriveRootGameDirectory_IsAccepted()
    {
        // CA-14: ドライブ直下のゲームフォルダでも、配下の相対パスを「外を指すパス」として拒否しない（純粋なパス計算のみで、ドライブには触れない）
        Assert.Equal(@"C:\www\js\plugins.js", GamePaths.ToFullPath(@"C:\", "www/js/plugins.js"));
        Assert.Equal(@"\\server\share\www\js\plugins.js", GamePaths.ToFullPath(@"\\server\share", "www/js/plugins.js"));

        // "C:" はそのドライブの現在のフォルダを指すため、期待値も同じ規則（GetFullPath）で求める
        var driveCurrent = Path.GetFullPath("C:");
        Assert.Equal(Path.Combine(driveCurrent, "www", "js", "plugins.js"), GamePaths.ToFullPath("C:", "www/js/plugins.js"));
        Assert.True(GamePaths.IsUnderDirectory(Path.Combine(driveCurrent, "www"), "C:"));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Game", @"C:\Game\")]
    [InlineData(@"C:\Game\", @"C:\Game\")]
    [InlineData(@"\\server\share", @"\\server\share\")]
    public void WithTrailingSeparator_AddsExactlyOneSeparator(string fullDirectory, string expected)
    {
        Assert.Equal(expected, GamePaths.WithTrailingSeparator(fullDirectory));
    }

    [Fact]
    public void AncestorDirectories_ReturnsShallowFirst()
    {
        Assert.Equal(["www", "www/sentience_jp", "www/sentience_jp/fonts"], GamePaths.AncestorDirectories("www/sentience_jp/fonts/a.ttf"));
        Assert.Empty(GamePaths.AncestorDirectories("nw.exe"));
    }

    [Theory]
    [InlineData("www/js/plugins.js.")]
    [InlineData("www/js/plugins.js...")]
    [InlineData("www/js/plugins.js ")]
    [InlineData("www/ /a.js")]
    [InlineData("www/.../a.js")]
    [InlineData("nw.exe.")]
    [InlineData("www/CON")]
    [InlineData("www/con.json")]
    [InlineData("www/NUL .txt")]
    [InlineData("www/COM1.js")]
    [InlineData("www/lpt9")]
    [InlineData("www/CONIN$")]
    [InlineData("www/PLUGIN~1.JS")]
    [InlineData("WWW~2/a.js")]
    public void DescribeProblem_RejectsWindowsAliases(string path)
    {
        // 末尾の "." や空白は取り除かれて別のファイルを指し、予約デバイス名や 8.3 形式の短い名前も既存のファイル・デバイスを指しうる（C-03）
        Assert.NotNull(GamePaths.DescribeProblem(path));
    }

    [Theory]
    [InlineData("www/.gitkeep")]
    [InlineData("www/sentience_jp/console.json")]
    [InlineData("www/sentience_jp/con_1.json")]
    [InlineData("www/sentience_jp/a~b.json")]
    [InlineData("www/sentience_jp/COM10.json")]
    public void DescribeProblem_AcceptsSimilarButSafeNames(string path)
    {
        Assert.Null(GamePaths.DescribeProblem(path));
    }

    [Theory]
    [InlineData("www/js/plugins/SentienceJP.js", true)]
    [InlineData("WWW/JS/PLUGINS/sentiencejp.js", true)]
    [InlineData("www/sentience_jp/translation.json", true)]
    [InlineData("www/sentience_jp/fonts/a.ttf", true)]
    [InlineData("www/sentience_jp", false)]
    [InlineData("www/sentience_jpx/a.json", false)]
    [InlineData("www/js/plugins.js", false)]
    [InlineData("www/js/rpg_core.js", false)]
    [InlineData("www/js/plugins/Other.js", false)]
    [InlineData("sentience_jp_backup/state.json", false)]
    [InlineData("www/sentience_jp/a.json.", false)]
    public void IsPatchFile_AllowsOnlyPluginFileAndPatchFolder(string path, bool expected)
    {
        Assert.Equal(expected, GamePaths.IsPatchFile(path));
    }

    [Theory]
    [InlineData("www", true)]
    [InlineData("www/js", true)]
    [InlineData("www/js/plugins", true)]
    [InlineData("www/sentience_jp", true)]
    [InlineData("www/sentience_jp/fonts", true)]
    [InlineData("www/data", false)]
    [InlineData("www/js/libs", false)]
    [InlineData("sentience_jp_backup", false)]
    [InlineData("", false)]
    public void IsPatchDirectory_AllowsOnlyPluginParentsAndPatchFolder(string path, bool expected)
    {
        Assert.Equal(expected, GamePaths.IsPatchDirectory(path));
    }
}
