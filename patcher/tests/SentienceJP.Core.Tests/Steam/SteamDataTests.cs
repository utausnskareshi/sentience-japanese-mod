using SentienceJP.Core.Steam;

namespace SentienceJP.Core.Tests.Steam;

/// <summary>libraryfolders.vdf と appmanifest の解析のテスト。</summary>
public sealed class SteamDataTests
{
    /// <summary>新形式の libraryfolders.vdf（apps 一覧に対象ゲームが載っていない例）。</summary>
    private const string NewFormatVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"277286956"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"219990"		"16858247446"
        		}
        	}
        }
        """;

    [Fact]
    public void ParseLibraryPaths_NewFormat_ReturnsAllPaths()
    {
        var paths = SteamLibraryFolders.ParseLibraryPaths(NewFormatVdf);

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], paths);
    }

    [Fact]
    public void ParseLibraryPaths_OldFormat_ReturnsPathsAndIgnoresOtherKeys()
    {
        const string oldFormat = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1234"
            	"ContentStatsID"		"-5"
            	"1"		"D:\\Games\\Steam"
            }
            """;

        var paths = SteamLibraryFolders.ParseLibraryPaths(oldFormat);

        Assert.Equal([@"D:\Games\Steam"], paths);
    }

    [Fact]
    public void ParseLibraryPaths_MissingBlock_Throws()
    {
        Assert.Throws<KeyValuesParseException>(() => SteamLibraryFolders.ParseLibraryPaths("\"other\" { }"));
    }

    [Fact]
    public void AppManifest_Parse_ReadsFields()
    {
        const string acf = """
            "AppState"
            {
            	"appid"		"635850"
            	"name"		"Sentience: The Android's Tale"
            	"StateFlags"		"4"
            	"installdir"		"Sentience The Android's Tale"
            	"buildid"		"2476493"
            }
            """;

        var manifest = AppManifest.Parse(acf);

        Assert.Equal("635850", manifest.AppId);
        Assert.Equal("Sentience The Android's Tale", manifest.InstallDir);
        Assert.Equal("2476493", manifest.BuildId);
        Assert.Equal(4, manifest.StateFlags);
        Assert.True(manifest.IsFullyInstalled);
    }

    [Fact]
    public void AppManifest_Parse_NonNumericStateFlags_IsNull()
    {
        var manifest = AppManifest.Parse("\"AppState\" { \"appid\" \"1\" \"installdir\" \"x\" \"StateFlags\" \"abc\" }");

        Assert.Null(manifest.StateFlags);
        Assert.False(manifest.IsFullyInstalled);
    }

    [Theory]
    [InlineData("\"LastUpdated\" \"1763856000\"", 1763856000L)]
    [InlineData("\"LastUpdated\" \"never\"", null)]
    [InlineData("", null)]
    public void AppManifest_Parse_ReadsLastUpdated(string field, long? expected)
    {
        // 複数のライブラリに appmanifest がある場合の選択に使う（C-13）
        var manifest = AppManifest.Parse($"\"AppState\" {{ \"appid\" \"1\" \"installdir\" \"x\" {field} }}");

        Assert.Equal(expected, manifest.LastUpdated);
    }

    [Theory]
    [InlineData("\"AppState\" { \"appid\" \"1\" }", "installdir")]
    [InlineData("\"AppState\" { \"installdir\" \"x\" }", "appid")]
    [InlineData("\"Other\" { }", "AppState")]
    public void AppManifest_Parse_MissingFields_Throws(string acf, string expectedFragment)
    {
        var ex = Assert.Throws<KeyValuesParseException>(() => AppManifest.Parse(acf));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }
}
