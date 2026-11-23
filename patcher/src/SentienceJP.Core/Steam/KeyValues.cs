using System.Text;

namespace SentienceJP.Core.Steam;

/// <summary>KeyValues（VDF）の値。文字列かオブジェクトのどちらか。</summary>
public abstract record KvValue;

/// <summary>KeyValues の文字列値。</summary>
/// <param name="Value">エスケープ解除済みの文字列。</param>
public sealed record KvString(string Value) : KvValue;

/// <summary>KeyValues のキーと値の組。</summary>
/// <param name="Key">キー（エスケープ解除済み）。</param>
/// <param name="Value">値。</param>
public sealed record KvPair(string Key, KvValue Value);

/// <summary>
/// KeyValues のオブジェクト（波括弧で囲まれたブロック）。
/// 同じキーが複数回現れることがあるため、辞書ではなく出現順のリストで保持する。
/// </summary>
/// <param name="Children">子要素（出現順）。</param>
public sealed record KvObject(IReadOnlyList<KvPair> Children) : KvValue
{
    /// <summary>キーに一致する最初の子要素の値を返す（Steam の慣例に合わせて大文字小文字を区別しない）。</summary>
    /// <param name="key">探すキー。</param>
    public KvValue? Get(string key) =>
        Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>キーに一致する文字列値を返す。無い・文字列でない場合は null。</summary>
    /// <param name="key">探すキー。</param>
    public string? GetString(string key) => (Get(key) as KvString)?.Value;

    /// <summary>キーに一致するオブジェクト値を返す。無い・オブジェクトでない場合は null。</summary>
    /// <param name="key">探すキー。</param>
    public KvObject? GetObject(string key) => Get(key) as KvObject;
}

/// <summary>KeyValues（VDF/ACF）の構文エラー。行・列番号を含む日本語のメッセージを持つ。</summary>
public sealed class KeyValuesParseException : FormatException
{
    /// <summary>構文エラーを作る。</summary>
    /// <param name="message">エラーの説明。</param>
    public KeyValuesParseException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Steam の KeyValues テキスト形式（libraryfolders.vdf、appmanifest_*.acf）の最小限のパーサ。
/// 対応する構文: ダブルクォート文字列（\\ \" \n \t のエスケープ）、クォート無しトークン、波括弧ブロック、// コメント。
/// 状態を持たない純粋関数として実装する（入力文字列 → 構文木）。
/// </summary>
public static class KeyValuesParser
{
    /// <summary>
    /// 波括弧ブロックの入れ子の深さの上限（CA-02）。
    /// 解析は再帰で行うため、壊れた・改変された vdf/acf の深い入れ子でスタックを使い切ると、プロセスごと即座に終了してしまう
    /// （StackOverflow は捕捉できない）。実際の libraryfolders.vdf・appmanifest の深さは 3〜4 程度なので、十分な余裕を持たせた値にする。
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>トークンの種類。</summary>
    private enum TokenKind
    {
        String,
        OpenBrace,
        CloseBrace,
        End,
    }

    /// <summary>字句解析の結果（種類・文字列値・位置）。</summary>
    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    /// <summary>
    /// テキスト全体を解析し、最上位のキーと値の並びを 1 つのオブジェクトとして返す。
    /// </summary>
    /// <param name="text">VDF/ACF ファイルの内容。</param>
    /// <exception cref="KeyValuesParseException">構文が不正な場合。</exception>
    public static KvObject Parse(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var root = ParsePairs(tokens, ref position, isTopLevel: true, depth: 0);
        return root;
    }

    /// <summary>
    /// キーと値の並びを解析する。トップレベルでは終端まで、ブロック内では閉じ括弧まで読む。
    /// </summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="position">読み取り位置（読み進めた分だけ更新する）。</param>
    /// <param name="isTopLevel">最上位の並びか。</param>
    /// <param name="depth">この並びを囲む波括弧ブロックの数（最上位は 0）。</param>
    private static KvObject ParsePairs(IReadOnlyList<Token> tokens, ref int position, bool isTopLevel, int depth)
    {
        var children = new List<KvPair>();
        while (true)
        {
            var token = tokens[position];
            switch (token.Kind)
            {
                case TokenKind.End when isTopLevel:
                    return new KvObject(children);
                case TokenKind.End:
                    throw new KeyValuesParseException($"閉じ括弧 '}}' が足りません（{token.Line} 行目 {token.Column} 列でファイルが終わりました）。");
                case TokenKind.CloseBrace when !isTopLevel:
                    position++;
                    return new KvObject(children);
                case TokenKind.CloseBrace:
                    throw new KeyValuesParseException($"対応しない閉じ括弧 '}}' があります（{token.Line} 行目 {token.Column} 列）。");
                case TokenKind.OpenBrace:
                    throw new KeyValuesParseException($"キーの位置に '{{' があります（{token.Line} 行目 {token.Column} 列）。");
                case TokenKind.String:
                    position++;
                    var value = ParseValue(tokens, ref position, token, depth);
                    children.Add(new KvPair(token.Text, value));
                    break;
                default:
                    throw new KeyValuesParseException($"想定外のトークンです（{token.Line} 行目 {token.Column} 列）。");
            }
        }
    }

    /// <summary>キーの直後に続く値（文字列またはブロック）を解析する。</summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="position">読み取り位置（読み進めた分だけ更新する）。</param>
    /// <param name="keyToken">値に対応するキーのトークン（エラーメッセージ用）。</param>
    /// <param name="depth">キーが置かれている並びを囲む波括弧ブロックの数。</param>
    private static KvValue ParseValue(IReadOnlyList<Token> tokens, ref int position, Token keyToken, int depth)
    {
        var token = tokens[position];
        switch (token.Kind)
        {
            case TokenKind.String:
                position++;
                return new KvString(token.Text);
            case TokenKind.OpenBrace:
                // 深さの判定は再帰に入る前に行う（上限を超える入力でも、スタックを使い切る前に必ず例外で止める）
                if (depth + 1 > MaxDepth)
                {
                    throw new KeyValuesParseException(
                        $"入れ子が深すぎます（{token.Line} 行目 {token.Column} 列で {MaxDepth} 段を超えました）。ファイルが壊れている可能性があります。");
                }

                position++;
                return ParsePairs(tokens, ref position, isTopLevel: false, depth + 1);
            default:
                throw new KeyValuesParseException(
                    $"キー \"{keyToken.Text}\"（{keyToken.Line} 行目 {keyToken.Column} 列）に対応する値がありません。");
        }
    }

    /// <summary>テキストをトークン列に分解する。末尾には必ず End トークンを置く。</summary>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        var line = 1;
        var lineStart = 0;

        while (index < text.Length)
        {
            var c = text[index];

            // 改行は行番号の更新だけ行う
            if (c == '\n')
            {
                index++;
                line++;
                lineStart = index;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            // // から行末まではコメント
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            var column = index - lineStart + 1;
            switch (c)
            {
                case '{':
                    tokens.Add(new Token(TokenKind.OpenBrace, "{", line, column));
                    index++;
                    break;
                case '}':
                    tokens.Add(new Token(TokenKind.CloseBrace, "}", line, column));
                    index++;
                    break;
                case '"':
                    tokens.Add(new Token(TokenKind.String, ReadQuoted(text, ref index, ref line, ref lineStart), line, column));
                    break;
                default:
                    tokens.Add(new Token(TokenKind.String, ReadUnquoted(text, ref index), line, column));
                    break;
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, line, index - lineStart + 1));
        return tokens;
    }

    /// <summary>ダブルクォートで囲まれた文字列を読み、エスケープを解除して返す。</summary>
    private static string ReadQuoted(string text, ref int index, ref int line, ref int lineStart)
    {
        var startLine = line;
        var startColumn = index - lineStart + 1;
        var builder = new StringBuilder();
        index++; // 開きクォートを読み飛ばす

        while (index < text.Length)
        {
            var c = text[index];
            if (c == '"')
            {
                index++;
                return builder.ToString();
            }

            if (c == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                switch (next)
                {
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '"':
                        builder.Append('"');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    default:
                        // 未知のエスケープはそのまま残す（Steam の実装に合わせ、情報を失わない）
                        builder.Append('\\').Append(next);
                        break;
                }

                index += 2;
                continue;
            }

            if (c == '\n')
            {
                line++;
                lineStart = index + 1;
            }

            builder.Append(c);
            index++;
        }

        throw new KeyValuesParseException($"文字列が閉じられていません（{startLine} 行目 {startColumn} 列から開始）。");
    }

    /// <summary>クォート無しのトークンを、空白・括弧・クォートの手前まで読む。</summary>
    private static string ReadUnquoted(string text, ref int index)
    {
        var start = index;
        while (index < text.Length)
        {
            var c = text[index];
            if (char.IsWhiteSpace(c) || c is '{' or '}' or '"')
            {
                break;
            }

            index++;
        }

        return text[start..index];
    }
}
