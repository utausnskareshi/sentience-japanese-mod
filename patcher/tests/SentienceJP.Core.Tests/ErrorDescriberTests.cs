using SentienceJP.Core.Patching;
using SentienceJP.Core.Platform;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests;

/// <summary>
/// よく起きる例外を日本語の説明に置き換える純粋関数（AA-13）と、それを使う利用者向けの表示のテスト。
/// 英語の原文は括弧書きで残る（情報を失わない）ことも確かめる。
/// </summary>
public sealed class ErrorDescriberTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>種類ごとの例外と、説明に含まれるべき日本語。</summary>
    public static TheoryData<Exception, string> KnownErrors() => new()
    {
        { new UnauthorizedAccessException("Access to the path 'C:\\Game\\www' is denied."), "アクセスが拒否されました" },
        { new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020)), "他のプログラムがファイルを使用しています" },
        { new IOException("The process cannot access the file because another process has locked a portion of the file.", unchecked((int)0x80070021)), "他のプログラムがファイルの一部をロックしています" },
        { new FileNotFoundException("Could not find file 'C:\\Game\\www\\js\\plugins.js'."), "ファイルが見つかりません" },
        { new DirectoryNotFoundException("Could not find a part of the path 'C:\\Game'."), "フォルダが見つかりません" },
        { new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)), "ディスクの空き容量が足りません" },
        { new IOException("There is not enough space on the disk.", unchecked((int)0x80070027)), "ディスクの空き容量が足りません" },
        { new PathTooLongException("The specified file name or path is too long."), "パスが長すぎます" },
        { new IOException("The filename or extension is too long.", unchecked((int)0x800700CE)), "パスが長すぎます" },
        { new DriveNotFoundException("Could not find the drive 'E:\\'."), "ドライブが見つかりません" },
        { new IOException("The device is not ready.", unchecked((int)0x80070015)), "ドライブの準備ができていません" },
        { new IOException("The media is write protected.", unchecked((int)0x80070013)), "ドライブが書き込み禁止になっています" },
        { new IOException("Some other I/O error."), "ファイルの読み書きでエラーが発生しました" },
    };

    [Theory]
    [MemberData(nameof(KnownErrors))]
    public void Describe_KnownErrors_GivesJapaneseWithOriginalInParentheses(Exception error, string expectedJapanese)
    {
        var described = ErrorDescriber.Describe(error);

        Assert.StartsWith(expectedJapanese, described, StringComparison.Ordinal);
        Assert.EndsWith($"（{error.Message}）", described, StringComparison.Ordinal);
        Assert.Equal(expectedJapanese, ErrorDescriber.Explain(error)?[..expectedJapanese.Length]);
    }

    [Fact]
    public void Describe_AlreadyJapaneseOrPatchException_IsReturnedAsIs()
    {
        // 既に日本語の説明（このツールのメッセージ・OS の日本語メッセージ）は、二重に説明しない
        Assert.Equal("状態ファイルが壊れています。", ErrorDescriber.Describe(new PatchException("状態ファイルが壊れています。")));
        Assert.Equal("使用中", ErrorDescriber.Describe(new IOException("使用中", unchecked((int)0x80070020))));
        Assert.Equal("管理者として実行できません。", ErrorDescriber.Describe(new System.ComponentModel.Win32Exception(5, "管理者として実行できません。")));
    }

    [Fact]
    public void Describe_UnknownExceptionType_ReturnsOriginalMessage()
    {
        Assert.Equal("Something unexpected.", ErrorDescriber.Describe(new InvalidOperationException("Something unexpected.")));
        Assert.Null(ErrorDescriber.Explain(new InvalidOperationException("x")));
    }

    [Fact]
    public void Apply_ReplacementBlockedByOtherProgram_ReportsJapaneseCauseKeepingOriginal()
    {
        // AA-13: 「原因: …」に .NET の英語の例外メッセージをそのまま出さず、日本語の説明に英語の原文を添える
        var game = FakeGame.CreateGame(_temp.Combine("game"));
        var payload = FakeGame.CreatePayload(_temp.Combine("payload"));
        var plugins = GamePaths.ToFullPath(game, GameConstants.PluginsJsRelativePath);

        PatchOperationResult result;
        using (new FileStream(plugins, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = PatchApplier.Apply(game, null, payload, _ => { });
        }

        Assert.False(result.Success);
        Assert.Contains("原因: アクセスが拒否されました", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.Error!.Message, result.Message, StringComparison.Ordinal);

        // LT-02: 置き換えが他のプログラムに妨げられても Windows はアクセス拒否を返すため、原因の候補にそれが挙がっていること
        Assert.Contains("他のプログラムがファイルを開いている", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileLogger_WriteFailure_IsDescribedInJapanese()
    {
        // AA-13: ログの書き込み失敗の表示も、日本語の説明（英語の原文は括弧書き）にする
        var blocker = _temp.Combine("not_a_folder");
        File.WriteAllText(blocker, "ログフォルダの場所にファイルがある");
        var logger = new FileLogger(blocker);

        logger.Info("書けない");

        Assert.NotNull(logger.LastError);
        Assert.Contains("ログを書き込めませんでした", logger.LastError, StringComparison.Ordinal);
        Assert.Contains("ファイルの読み書きでエラーが発生しました", logger.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAccessAdvice_ListsJapaneseReasonsOnly()
    {
        // AA-13: 書き込み権限の案内は日本語の理由だけを並べる（原文は Detail に残し、ログに書く）
        var problem = new WriteAccessProblem(
            "www",
            ErrorDescriber.Explain(new UnauthorizedAccessException("x"))!,
            WriteAccessProblemKind.AccessDenied,
            "Access to the path 'C:\\Game\\www\\sentience_jp_write_test_0123.tmp' is denied.");

        var advice = WriteAccessChecker.Advise([problem], isElevated: false);

        Assert.Contains("www: アクセスが拒否されました", advice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentience_jp_write_test", advice.Message, StringComparison.Ordinal);
        Assert.Contains("sentience_jp_write_test", problem.ToString(), StringComparison.Ordinal);
    }
}
