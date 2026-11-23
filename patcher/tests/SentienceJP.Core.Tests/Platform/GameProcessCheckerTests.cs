using SentienceJP.Core.Platform;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Platform;

/// <summary>
/// ゲームの起動状況の判定（CA-09）のテスト。
/// - 判定の純粋関数（<see cref="GameProcessPathMatcher"/>）を、実体のパスを返す偽の関数で確かめる。
/// - 実体のパスの取得（GetFinalPathNameByHandle）を、本物のジャンクションで確かめる。
/// - 実行中のこのテストプロセス自身を「ゲーム」に見立て、ジャンクション経由のフォルダでも起動中と判定できることを確かめる
///   （偽の nw.exe を起動しない。プロセス名だけを差し替える）。
/// </summary>
public sealed class GameProcessCheckerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly JunctionScope _junctions = new();

    /// <inheritdoc />
    public void Dispose()
    {
        _junctions.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Classify_SameNotation_IsRunning_WithoutResolving()
    {
        var state = GameProcessPathMatcher.Classify(@"D:\Games\Sentience\nw.exe", @"D:\Games\Sentience", null, _ => throw new InvalidOperationException("呼ばれないはず"));

        Assert.Equal(GameProcessState.Running, state);
    }

    [Theory]
    [InlineData(@"D:\Real\Game\nw.exe", @"D:\Link\Game")]
    [InlineData(@"D:\Link\Game\nw.exe", @"D:\Real\Game")]
    public void Classify_DifferentNotationOfSameFolder_IsRunningByFinalPath(string imagePath, string gameDirectory)
    {
        // CA-09: ジャンクション経由（どちらの側がリンクでも）、実体のパス同士で比べて起動中と判定する
        var final = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:\Real\Game\nw.exe"] = @"D:\Real\Game\nw.exe",
            [@"D:\Link\Game\nw.exe"] = @"D:\Real\Game\nw.exe",
        };

        var state = GameProcessPathMatcher.Classify(imagePath, gameDirectory, @"D:\Real\Game", path => final.GetValueOrDefault(path));

        Assert.Equal(GameProcessState.Running, state);
    }

    [Theory]
    [InlineData(@"D:\Other\Game\nw.exe")]
    [InlineData(@"D:\Real\Game2\nw.exe")]
    public void Classify_UnrelatedProcess_IsNotRunning(string finalImagePath)
    {
        var state = GameProcessPathMatcher.Classify(@"E:\x\nw.exe", @"D:\Link\Game", @"D:\Real\Game", _ => finalImagePath);

        Assert.Equal(GameProcessState.NotRunning, state);
    }

    [Fact]
    public void Classify_GameFolderCannotBeResolved_FallsBackToNotationComparison()
    {
        // CA-09: ゲームフォルダの実体が分からなければ、従来の（表記どおりの）判定のまま。他のゲームの nw.exe をすべて判定不能にしない
        var state = GameProcessPathMatcher.Classify(@"E:\OtherGame\nw.exe", @"D:\Game", null, _ => throw new InvalidOperationException("呼ばれないはず"));

        Assert.Equal(GameProcessState.NotRunning, state);
    }

    [Fact]
    public void Classify_ProcessImageCannotBeResolved_IsUnknown_NeverRunning()
    {
        // CA-09: プロセスの実行ファイルの実体が分からなければ「判定不能」（利用者に確認を求める）。誤って「起動中」にはしない
        var state = GameProcessPathMatcher.Classify(@"E:\x\nw.exe", @"D:\Link\Game", @"D:\Real\Game", _ => null);

        Assert.Equal(GameProcessState.Unknown, state);
    }

    [Theory]
    [InlineData(@"\\?\C:\Games\Sentience", @"C:\Games\Sentience")]
    [InlineData(@"\\?\UNC\server\share\Games", @"\\server\share\Games")]
    [InlineData(@"\\?\unc\server\share", @"\\server\share")]
    [InlineData(@"C:\Games\Sentience", @"C:\Games\Sentience")]
    public void StripExtendedPrefix_ReturnsNormalNotation(string finalPath, string expected)
    {
        Assert.Equal(expected, GameProcessPathMatcher.StripExtendedPrefix(finalPath));
    }

    [Fact]
    public void TryResolveFinalPath_Junction_ResolvesToSameRealFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var real = _temp.Combine("RealGame");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "nw.exe"), "fake");
        var link = _temp.Combine("LinkedGame");
        _junctions.Create(link, real);

        var resolvedReal = GameProcessChecker.TryResolveFinalPath(real);
        var resolvedLink = GameProcessChecker.TryResolveFinalPath(link);
        var resolvedFileViaLink = GameProcessChecker.TryResolveFinalPath(Path.Combine(link, "nw.exe"));

        Assert.NotNull(resolvedReal);
        Assert.Equal(resolvedReal, resolvedLink, ignoreCase: true);
        Assert.EndsWith("RealGame", resolvedReal, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(resolvedReal, "nw.exe"), resolvedFileViaLink, ignoreCase: true);
        Assert.DoesNotContain(@"\\?\", resolvedReal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("nul-char")]
    public void TryResolveFinalPath_Unresolvable_ReturnsNull(string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = kind switch
        {
            "missing" => _temp.Combine("does_not_exist"),
            "nul-char" => _temp.FullPath + "\0x",
            _ => string.Empty,
        };

        Assert.Null(GameProcessChecker.TryResolveFinalPath(path));
    }

    [Fact]
    public void Check_RunningProcessViaJunction_IsDetected()
    {
        // CA-09: 実行中のこのテストプロセスをゲームに見立て、その実行ファイルのフォルダへのジャンクション経由でも「起動中」と判定する
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        var exeDirectory = Path.GetDirectoryName(exe)!;
        var processName = Path.GetFileNameWithoutExtension(exe);
        var link = _temp.Combine("LinkToProcessFolder");
        _junctions.Create(link, exeDirectory);
        var checker = new GameProcessChecker(processName);

        Assert.Equal(GameProcessState.Running, checker.Check(exeDirectory));
        Assert.Equal(GameProcessState.Running, checker.Check(link));
        Assert.NotEqual(GameProcessState.Running, checker.Check(_temp.Combine("Unrelated")));
    }

    [Fact]
    public void Check_ProcessNameThatIsNotRunning_IsNotRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var checker = new GameProcessChecker($"sentience_jp_no_such_process_{Guid.NewGuid():N}");

        Assert.Equal(GameProcessState.NotRunning, checker.Check(_temp.FullPath));
    }
}
