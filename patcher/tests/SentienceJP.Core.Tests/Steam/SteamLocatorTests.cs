using SentienceJP.Core.Steam;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Steam;

/// <summary>
/// ライブラリ探索のテスト。テンポラリフォルダに Steam と同じ構成を作って検証する。
/// </summary>
public sealed class SteamLocatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Locate_FindsGameInSecondaryLibrary_EvenIfNotListedInApps()
    {
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("SteamLibrary");
        WriteLibraryFolders(steamRoot, steamRoot, library);
        WriteAppManifest(library, "Sentience The Android's Tale");
        FakeGame.CreateGame(Path.Combine(library, "steamapps", "common", "Sentience The Android's Tale"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "Sentience The Android's Tale"), result.Installation.GameDirectory);
        Assert.Equal("2476493", result.Installation.Manifest.BuildId);
    }

    [Fact]
    public void Locate_AcceptsRegistryStylePathWithForwardSlashes()
    {
        var steamRoot = _temp.Combine("Steam");
        WriteLibraryFolders(steamRoot, steamRoot);
        WriteAppManifest(steamRoot, "Game");
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game"));

        // レジストリの SteamPath は "c:/program files (x86)/steam" のようにスラッシュ区切りで記録されている
        var registryStyle = steamRoot.Replace('\\', '/');
        var result = SteamLocator.Locate([registryStyle], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
    }

    [Fact]
    public void Locate_ManifestExistsButGameFilesMissing_ReturnsNullWithDiagnostic()
    {
        var steamRoot = _temp.Combine("Steam");
        WriteLibraryFolders(steamRoot, steamRoot);
        WriteAppManifest(steamRoot, "Game");
        Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.Null(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("必要なファイルがありません", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_NoManifest_ReturnsNull()
    {
        var steamRoot = _temp.Combine("Steam");
        WriteLibraryFolders(steamRoot, steamRoot);

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.Null(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("見つかりませんでした", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_CorruptLibraryFolders_StillSearchesSteamRoot()
    {
        var steamRoot = _temp.Combine("Steam");
        FakeGame.WriteText(steamRoot, "steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"0\" {");
        WriteAppManifest(steamRoot, "Game");
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("libraryfolders.vdf を読み取れませんでした", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_DeeplyNestedVdfAndAcf_RecordsDiagnosticsInsteadOfCrashing()
    {
        // CA-02: 深い入れ子の libraryfolders.vdf / appmanifest があっても起動処理が落ちず、診断に記録して続行する
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("Lib");
        var deep = string.Concat(Enumerable.Repeat("\"k\" { ", 5000)) + string.Concat(Enumerable.Repeat("} ", 5000));
        FakeGame.WriteText(steamRoot, "steamapps/libraryfolders.vdf", $"\"libraryfolders\" {{ {deep} }}");
        FakeGame.WriteText(library, $"steamapps/appmanifest_{GameConstants.SteamAppId}.acf", $"\"AppState\" {{ {deep} }}");
        WriteAppManifest(steamRoot, "Game");
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot, library], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("libraryfolders.vdf を読み取れませんでした", StringComparison.Ordinal) && d.Contains("入れ子が深すぎます", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Contains("appmanifest を読み取れませんでした", StringComparison.Ordinal) && d.Contains("入れ子が深すぎます", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_NonexistentSteamRoot_ReturnsNull()
    {
        var result = SteamLocator.Locate([_temp.Combine("NoSteam")], GameConstants.SteamAppId);

        Assert.Null(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("存在しません", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_GameInThirdOfManyLibraries_IsFound()
    {
        var steamRoot = _temp.Combine("Steam");
        var libraryB = _temp.Combine("LibB");
        var libraryC = _temp.Combine("LibC");
        WriteLibraryFolders(steamRoot, steamRoot, libraryB, libraryC);
        Directory.CreateDirectory(Path.Combine(libraryB, "steamapps"));
        WriteAppManifest(libraryC, "Game");
        FakeGame.CreateGame(Path.Combine(libraryC, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.GetFullPath(libraryC), result.Installation.LibraryDirectory);
        Assert.Contains(result.Diagnostics, d => d.Contains("探索するライブラリ（3 件）", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_OldFormatLibraryFolders_FindsGame()
    {
        // 旧形式の libraryfolders.vdf（番号の値がそのままパス）
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("OldLib");
        FakeGame.WriteText(
            steamRoot,
            "steamapps/libraryfolders.vdf",
            $"\"LibraryFolders\"\n{{\n\t\"TimeNextStatsReport\"\t\t\"1234\"\n\t\"1\"\t\t\"{library.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n}}\n");
        WriteAppManifest(library, "Game");
        FakeGame.CreateGame(Path.Combine(library, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.GetFullPath(library), result.Installation.LibraryDirectory);
    }

    [Fact]
    public void Locate_SameLibraryListedTwice_IsSearchedOnce()
    {
        // レジストリ・vdf で表記（大文字小文字・末尾の区切り・"/"）が違っても、同じライブラリは 1 回だけ探す
        var steamRoot = _temp.Combine("Steam");
        WriteLibraryFolders(steamRoot, steamRoot.ToUpperInvariant() + "\\", steamRoot.Replace('\\', '/'));
        WriteAppManifest(steamRoot, "Game");
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot, steamRoot.ToLowerInvariant()], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("探索するライブラリ（1 件）", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_ManifestsInTwoLibraries_PrefersFullyInstalled()
    {
        // C-13: 移動前の古い appmanifest（更新待ちの状態）が別のライブラリに残っていても、インストール完了状態の方を選ぶ
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("Lib");
        WriteLibraryFolders(steamRoot, steamRoot, library);
        WriteAppManifest(steamRoot, "Game", stateFlags: "6", lastUpdated: "1800000000");
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game"));
        WriteAppManifest(library, "Game", stateFlags: "4", lastUpdated: "1700000000");
        FakeGame.CreateGame(Path.Combine(library, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.GetFullPath(library), result.Installation.LibraryDirectory);
        Assert.Contains(result.Diagnostics, d => d.Contains("複数の候補から選びました", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_ManifestsInTwoLibraries_PrefersNewerLastUpdated()
    {
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("Lib");
        WriteLibraryFolders(steamRoot, steamRoot, library);
        WriteAppManifest(steamRoot, "Game", lastUpdated: "1700000000");
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game"));
        WriteAppManifest(library, "Game", lastUpdated: "1800000000");
        FakeGame.CreateGame(Path.Combine(library, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.GetFullPath(library), result.Installation.LibraryDirectory);
        Assert.Equal((long?)1800000000, result.Installation.Manifest.LastUpdated);
    }

    [Fact]
    public void Locate_StaleManifestWithoutGameFiles_UsesTheOtherLibrary()
    {
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("Lib");
        WriteLibraryFolders(steamRoot, steamRoot, library);
        WriteAppManifest(steamRoot, "Game", lastUpdated: "1900000000");
        WriteAppManifest(library, "Game", lastUpdated: "1700000000");
        FakeGame.CreateGame(Path.Combine(library, "steamapps", "common", "Game"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.GetFullPath(library), result.Installation.LibraryDirectory);
        Assert.Contains(result.Diagnostics, d => d.Contains("必要なファイルがありません", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_ManifestFoundButNoUsableGame_SaysSoInsteadOfNotFound()
    {
        // 診断の文言: appmanifest はあったのにゲームフォルダが使えない場合に「見つかりませんでした」と紛らわしく書かない
        var steamRoot = _temp.Combine("Steam");
        WriteLibraryFolders(steamRoot, steamRoot);
        WriteAppManifest(steamRoot, "Game");

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.Null(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("1 件見つかりましたが", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("見つかりませんでした", StringComparison.Ordinal));
    }

    [Fact]
    public void Locate_InstallDirWithNul_SkipsThatLibraryAndFindsTheOther()
    {
        // CA-12: installdir に NUL がある壊れた appmanifest があっても例外を漏らさず、そのライブラリを飛ばして別のライブラリの正常なインストールを見つける
        var steamRoot = _temp.Combine("Steam");
        var library = _temp.Combine("Lib2");
        WriteLibraryFolders(steamRoot, steamRoot, library);
        WriteRawAppManifest(steamRoot, "Sentience\0broken");
        WriteAppManifest(library, "Sentience");
        FakeGame.CreateGame(Path.Combine(library, "steamapps", "common", "Sentience"));

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.NotNull(result.Installation);
        Assert.Equal(Path.GetFullPath(library), result.Installation.LibraryDirectory);
        Assert.Contains(result.Diagnostics, d => d.Contains("installdir が不正", StringComparison.Ordinal) && d.Contains("\\u0000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("..\\..\\..\\Elsewhere\\Game")]
    [InlineData("../Elsewhere")]
    [InlineData("ABSOLUTE")]
    [InlineData("..")]
    [InlineData("Game\\Sub")]
    public void Locate_InstallDirOutsideCommonFolder_IsRejected(string installDir)
    {
        // CA-13: installdir の ".."・絶対パス・区切り文字で、ライブラリの外（や配下の別の場所）のフォルダをゲームフォルダとして選ばない
        var steamRoot = _temp.Combine("Steam");
        WriteLibraryFolders(steamRoot, steamRoot);
        var elsewhere = FakeGame.CreateGame(_temp.Combine("Elsewhere/Game"));
        FakeGame.CreateGame(Path.Combine(steamRoot, "steamapps", "common", "Game", "Sub"));
        WriteRawAppManifest(steamRoot, installDir == "ABSOLUTE" ? elsewhere : installDir);

        var result = SteamLocator.Locate([steamRoot], GameConstants.SteamAppId);

        Assert.Null(result.Installation);
        Assert.Contains(result.Diagnostics, d => d.Contains("installdir が不正", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Sentience The Android's Tale")]
    [InlineData("Game (2024) - Deluxe")]
    [InlineData("日本語のゲーム")]
    [InlineData(".hidden")]
    public void DescribeInstallDirProblem_AcceptsSingleFolderNames(string installDir)
    {
        Assert.Null(SteamLocator.DescribeInstallDirProblem(installDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("C:Game")]
    [InlineData("C:\\Game")]
    [InlineData("\\\\server\\share")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a*b")]
    [InlineData("Game.")]
    [InlineData("Game ")]
    public void DescribeInstallDirProblem_RejectsNonFolderNames(string installDir)
    {
        // CA-12・CA-13: 区切り文字・".."・ルート付き・ドライブ指定・NUL などの制御文字・ワイルドカード・末尾の "." や空白を拒否する
        Assert.NotNull(SteamLocator.DescribeInstallDirProblem(installDir));
    }

    [Fact]
    public void ChooseBest_OrdersByInstalledThenLastUpdatedThenLibraryOrder()
    {
        var installedOld = Candidate("A", stateFlags: 4, lastUpdated: 100);
        var installedNew = Candidate("B", stateFlags: 4, lastUpdated: 200);
        var updatingNewest = Candidate("C", stateFlags: 6, lastUpdated: 300);
        var installedUnknown = Candidate("D", stateFlags: 4, lastUpdated: null);

        Assert.Same(installedNew, SteamLocator.ChooseBest([updatingNewest, installedOld, installedNew, installedUnknown]));
        Assert.Same(installedOld, SteamLocator.ChooseBest([installedOld, Candidate("E", stateFlags: 4, lastUpdated: 100)]));
        Assert.Same(updatingNewest, SteamLocator.ChooseBest([Candidate("F", stateFlags: null, lastUpdated: 1), updatingNewest]));
        Assert.Null(SteamLocator.ChooseBest([]));
    }

    /// <summary>選択のテスト用の候補を作る。</summary>
    private static GameInstallation Candidate(string name, int? stateFlags, long? lastUpdated) =>
        new($@"C:\{name}\game", $@"C:\{name}", $@"C:\{name}\steamapps\appmanifest.acf", new AppManifest("635850", null, "game", "1", stateFlags, lastUpdated));

    /// <summary>libraryfolders.vdf（新形式。apps 一覧には対象ゲームを載せない）を書く。</summary>
    private static void WriteLibraryFolders(string steamRoot, params string[] libraries)
    {
        var blocks = libraries.Select((path, index) =>
            $"\t\"{index}\"\n\t{{\n\t\t\"path\"\t\t\"{path.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n\t\t\"apps\"\n\t\t{{\n\t\t\t\"228980\"\t\t\"1\"\n\t\t}}\n\t}}\n");
        FakeGame.WriteText(steamRoot, "steamapps/libraryfolders.vdf", $"\"libraryfolders\"\n{{\n{string.Concat(blocks)}}}\n");
    }

    /// <summary>
    /// installdir をそのまま（"\" は KeyValues のエスケープとして "\\" に変換して）書いた appmanifest_635850.acf を作る（不正な installdir の再現用）。
    /// </summary>
    private static void WriteRawAppManifest(string library, string installDir) =>
        FakeGame.WriteText(
            library,
            $"steamapps/appmanifest_{GameConstants.SteamAppId}.acf",
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{GameConstants.SteamAppId}\"\n\t\"StateFlags\"\t\t\"4\"\n"
            + $"\t\"installdir\"\t\t\"{installDir.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n\t\"buildid\"\t\t\"2476493\"\n}}\n");

    /// <summary>appmanifest_635850.acf を書く。</summary>
    private static void WriteAppManifest(string library, string installDir, string stateFlags = "4", string? lastUpdated = null) =>
        FakeGame.WriteText(
            library,
            $"steamapps/appmanifest_{GameConstants.SteamAppId}.acf",
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{GameConstants.SteamAppId}\"\n\t\"StateFlags\"\t\t\"{stateFlags}\"\n\t\"installdir\"\t\t\"{installDir}\"\n"
            + (lastUpdated is null ? string.Empty : $"\t\"LastUpdated\"\t\t\"{lastUpdated}\"\n")
            + "\t\"buildid\"\t\t\"2476493\"\n}\n");
}
