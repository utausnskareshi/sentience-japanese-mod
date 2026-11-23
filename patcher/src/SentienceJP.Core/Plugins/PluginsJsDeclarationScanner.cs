namespace SentienceJP.Core.Plugins;

/// <summary>plugins.js の中の、コードとしての "var $plugins" の宣言の位置。</summary>
/// <param name="Start">"var" の先頭の位置（テキスト全体での文字インデックス）。</param>
/// <param name="NameEnd">"$plugins" の直後の位置。</param>
internal readonly record struct PluginsJsDeclaration(int Start, int NameEnd);

/// <summary>
/// plugins.js から、コードとしての "var $plugins" の宣言を探す純粋関数群（CA-06）。
/// コメント（// と /* */）と文字列リテラル（' " `。テンプレートリテラルの ${ … } の入れ子を含む）の中は読み飛ばし、
/// 宣言より前のコメントや文字列にある "var $plugins = [" を本物の宣言と取り違えない
/// （取り違えると、コメントの中へエントリを挿入してゲームのスクリプトを壊すのに「適用済み」と判定してしまう）。
/// 入れ子は明示的な積み重ね（リスト）で扱い、再帰しない（深い入れ子の入力でもスタックを使い切らない）。
/// 正規表現リテラルは扱わない（RPGツクールMV が出力する plugins.js には現れないため。現れた場合は誤って文字列として読み、
/// 形式エラーになるか宣言を見落とすだけで、ファイルは書き換えない）。
/// </summary>
internal static class PluginsJsDeclarationScanner
{
    /// <summary>宣言する変数名。</summary>
    private const string VariableName = "$plugins";

    /// <summary>JavaScript の行区切り（LINE SEPARATOR、U+2028）。ソース上で見分けやすいよう数値で書く。</summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>JavaScript の段落区切り（PARAGRAPH SEPARATOR、U+2029）。</summary>
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>ゼロ幅非接合子（ZWNJ、U+200C。識別子に使える）。</summary>
    private const char ZeroWidthNonJoiner = (char)0x200C;

    /// <summary>ゼロ幅接合子（ZWJ、U+200D。識別子に使える）。</summary>
    private const char ZeroWidthJoiner = (char)0x200D;

    /// <summary>
    /// コードとしての "var $plugins" の宣言を、出現順にすべて返す。
    /// "var" と "$plugins" の間には 1 文字以上の空白が必要で、"$plugins_extra" のように名前が続くものや、"myvar" の一部は数えない。
    /// </summary>
    /// <param name="text">plugins.js の内容。</param>
    /// <exception cref="PluginsJsFormatException">コメントや文字列リテラルが閉じられていない場合。</exception>
    public static IReadOnlyList<PluginsJsDeclaration> FindDeclarations(string text)
    {
        var found = new List<PluginsJsDeclaration>();

        // 読み取り中の文脈の積み重ね。InTemplate=true はテンプレートリテラルの本文、false はコード（${ … } の中を含む）。
        // コードの文脈には、その中で開いた波括弧の数（BraceDepth）を持ち、${ … } の終わりの '}' を見分ける。
        var frames = new List<(bool InTemplate, int BraceDepth)> { (false, 0) };
        var index = 0;
        while (index < text.Length)
        {
            var (inTemplate, braceDepth) = frames[^1];
            var c = text[index];
            if (inTemplate)
            {
                index = StepInTemplate(text, index, frames);
                continue;
            }

            if (c == '/' && index + 1 < text.Length && text[index + 1] is '/' or '*')
            {
                index = SkipComment(text, index);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    index = SkipQuotedString(text, index);
                    continue;
                case '`':
                    frames.Add((true, 0));
                    index++;
                    continue;
                case '{':
                    frames[^1] = (false, braceDepth + 1);
                    index++;
                    continue;
                case '}':
                    if (braceDepth > 0)
                    {
                        frames[^1] = (false, braceDepth - 1);
                    }
                    else if (frames.Count > 1)
                    {
                        // ${ … } の終わり: テンプレートリテラルの本文へ戻る
                        frames.RemoveAt(frames.Count - 1);
                    }

                    index++;
                    continue;
            }

            if (TryMatchDeclaration(text, index) is { } declaration)
            {
                found.Add(declaration);
                index = declaration.NameEnd;
                continue;
            }

            index++;
        }

        if (frames.Count > 1)
        {
            throw new PluginsJsFormatException("plugins.js のテンプレート文字列（`）が閉じられていません。");
        }

        return found;
    }

    /// <summary>
    /// 空白とコメントを読み飛ばした位置を返す（宣言の名前と '='、'=' と '[' の間に使う）。
    /// </summary>
    /// <param name="text">plugins.js の内容。</param>
    /// <param name="index">読み始める位置。</param>
    /// <exception cref="PluginsJsFormatException">ブロックコメントが閉じられていない場合。</exception>
    public static int SkipTrivia(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            else if (text[index] == '/' && index + 1 < text.Length && text[index + 1] is '/' or '*')
            {
                index = SkipComment(text, index);
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>テンプレートリテラルの本文を 1 歩読み進め、次の位置を返す（終わりの ` と ${ の始まりで文脈を切り替える）。</summary>
    private static int StepInTemplate(string text, int index, List<(bool InTemplate, int BraceDepth)> frames)
    {
        switch (text[index])
        {
            case '\\':
                // エスケープされた文字（\` や \$ を含む）は、文脈の切り替えに使わない
                return index + 2;
            case '`':
                frames.RemoveAt(frames.Count - 1);
                return index + 1;
            case '$' when index + 1 < text.Length && text[index + 1] == '{':
                frames.Add((false, 0));
                return index + 2;
            default:
                return index + 1;
        }
    }

    /// <summary>
    /// "//" か "/*" で始まるコメントを読み飛ばし、その直後の位置を返す。
    /// 行コメントは行末（\n・\r・U+2028・U+2029）の手前まで、ブロックコメントは "*/" の直後まで。
    /// </summary>
    /// <exception cref="PluginsJsFormatException">ブロックコメントが閉じられていない場合。</exception>
    private static int SkipComment(string text, int index)
    {
        if (text[index + 1] == '/')
        {
            var position = index + 2;
            while (position < text.Length && !IsLineTerminator(text[position]))
            {
                position++;
            }

            return position;
        }

        var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new PluginsJsFormatException($"plugins.js のブロックコメント（/* … */）が閉じられていません（{LineOf(text, index)} 行目から）。");
        }

        return end + 2;
    }

    /// <summary>
    /// ' または " で囲まれた文字列リテラルを読み飛ばし、閉じ引用符の直後の位置を返す。
    /// バックスラッシュのエスケープ（行の継続 \ + 改行を含む）を考慮し、エスケープされていない改行があれば閉じられていないとみなす。
    /// </summary>
    /// <exception cref="PluginsJsFormatException">文字列リテラルが閉じられていない場合。</exception>
    private static int SkipQuotedString(string text, int index)
    {
        var quote = text[index];
        var position = index + 1;
        while (position < text.Length)
        {
            var c = text[position];
            if (c == '\\')
            {
                // \r\n による行の継続は 2 文字でひとつの改行
                position += position + 2 < text.Length && text[position + 1] == '\r' && text[position + 2] == '\n' ? 3 : 2;
                continue;
            }

            if (c == quote)
            {
                return position + 1;
            }

            if (c is '\n' or '\r')
            {
                break;
            }

            position++;
        }

        throw new PluginsJsFormatException($"plugins.js の文字列リテラル（{quote}）が閉じられていません（{LineOf(text, index)} 行目から）。");
    }

    /// <summary>
    /// 指定した位置から "var" + 空白 + "$plugins" の宣言が始まっていれば、その位置を返す（それ以外は null）。
    /// "var" の直前と "$plugins" の直後が識別子の文字なら、別の名前の一部なので数えない。
    /// </summary>
    private static PluginsJsDeclaration? TryMatchDeclaration(string text, int index)
    {
        if (!text.AsSpan(index).StartsWith("var", StringComparison.Ordinal) || (index > 0 && IsIdentifierPart(text[index - 1])))
        {
            return null;
        }

        var position = index + 3;
        var whitespaceStart = position;
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        if (position == whitespaceStart || !text.AsSpan(position).StartsWith(VariableName, StringComparison.Ordinal))
        {
            return null;
        }

        var nameEnd = position + VariableName.Length;
        return nameEnd < text.Length && IsIdentifierPart(text[nameEnd]) ? null : new PluginsJsDeclaration(index, nameEnd);
    }

    /// <summary>JavaScript の識別子に使える文字か（英数字・Unicode の文字・"_"・"$"・ZWNJ・ZWJ。判定は近似）。</summary>
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c == ZeroWidthNonJoiner || c == ZeroWidthJoiner;

    /// <summary>JavaScript の行末の文字か（\n・\r・U+2028・U+2029。行コメントはここで終わる）。</summary>
    private static bool IsLineTerminator(char c) => c is '\n' or '\r' || c == LineSeparator || c == ParagraphSeparator;

    /// <summary>位置が何行目か（1 から。エラーメッセージ用）。</summary>
    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
