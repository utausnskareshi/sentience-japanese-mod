using SentienceJP.Core.Steam;

namespace SentienceJP.Core.Tests.Steam;

/// <summary>KeyValues（VDF/ACF）パーサのテスト。</summary>
public sealed class KeyValuesParserTests
{
    [Fact]
    public void Parse_NestedBlocks_ReturnsTree()
    {
        var root = KeyValuesParser.Parse("\"Root\"\n{\n\t\"Child\"\n\t{\n\t\t\"key\"\t\t\"value\"\n\t}\n\t\"other\" \"x\"\n}\n");

        var rootBlock = Assert.IsType<KvObject>(root.Get("Root"));
        Assert.Equal("value", rootBlock.GetObject("Child")?.GetString("key"));
        Assert.Equal("x", rootBlock.GetString("other"));
    }

    [Fact]
    public void Parse_UnescapesQuotedStrings()
    {
        var root = KeyValuesParser.Parse("\"path\" \"C:\\\\Program Files (x86)\\\\Steam\" \"q\" \"say \\\"hi\\\"\" \"u\" \"a\\\\b\\x\"");

        Assert.Equal(@"C:\Program Files (x86)\Steam", root.GetString("path"));
        Assert.Equal("say \"hi\"", root.GetString("q"));

        // 未知のエスケープ（\x）は情報を失わないようそのまま残す
        Assert.Equal(@"a\b\x", root.GetString("u"));
    }

    [Fact]
    public void Parse_IgnoresCommentsAndAcceptsUnquotedTokens()
    {
        var root = KeyValuesParser.Parse("// コメント\nRoot\n{\n  key value // 行末コメント\n}\n");

        Assert.Equal("value", root.GetObject("Root")?.GetString("key"));
    }

    [Fact]
    public void Parse_KeysAreCaseInsensitiveAndFirstDuplicateWins()
    {
        var root = KeyValuesParser.Parse("\"Key\" \"first\" \"key\" \"second\"");

        Assert.Equal("first", root.GetString("KEY"));
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public void Parse_NestingAtMaxDepth_Succeeds()
    {
        // CA-02: 上限ちょうどの深さまでは従来どおり解析できる
        var root = KeyValuesParser.Parse(Nested(KeyValuesParser.MaxDepth, closed: true));

        var current = root;
        for (var depth = 0; depth < KeyValuesParser.MaxDepth; depth++)
        {
            current = Assert.IsType<KvObject>(current.Get("k"));
        }

        Assert.Equal("leaf", current.GetString("v"));
    }

    [Fact]
    public void Parse_NestingBeyondMaxDepth_ThrowsParseException()
    {
        // CA-02: 上限を 1 段でも超えたら、構文エラーとして扱う（再帰でスタックを使い切らない）
        var ex = Assert.Throws<KeyValuesParseException>(() => KeyValuesParser.Parse(Nested(KeyValuesParser.MaxDepth + 1, closed: true)));

        Assert.Contains("入れ子が深すぎます", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_ExtremelyDeepNesting_ThrowsInsteadOfCrashingTheProcess(bool closed)
    {
        // CA-02: 10 万段の入れ子（壊れた・改変された vdf）でも、プロセスを落とさず例外で止まる。
        // 深さは再帰に入る前に判定するため、このテスト自体もスタックを使い切らない。
        var ex = Assert.Throws<KeyValuesParseException>(() => KeyValuesParser.Parse(Nested(100_000, closed)));

        Assert.Contains("入れ子が深すぎます", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"Root\" {", "閉じ括弧")]
    [InlineData("\"Root\" { \"a\" \"b\" } }", "対応しない閉じ括弧")]
    [InlineData("\"key\" \"unterminated", "閉じられていません")]
    [InlineData("\"key\"", "値がありません")]
    [InlineData("{ \"a\" \"b\" }", "'{'")]
    public void Parse_InvalidSyntax_ThrowsWithMessage(string text, string expectedFragment)
    {
        var ex = Assert.Throws<KeyValuesParseException>(() => KeyValuesParser.Parse(text));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 指定した段数だけ "k" { … } を入れ子にした KeyValues テキストを作る（最も内側には "v" "leaf" を置く）。
    /// </summary>
    /// <param name="depth">入れ子の段数。</param>
    /// <param name="closed">閉じ括弧も書くか（false なら開き括弧だけが続く壊れたファイル）。</param>
    private static string Nested(int depth, bool closed)
    {
        var builder = new System.Text.StringBuilder();
        builder.Insert(0, "\"k\" { ", depth).Append("\"v\" \"leaf\" ");
        if (closed)
        {
            builder.Insert(builder.Length, "} ", depth);
        }

        return builder.ToString();
    }
}
