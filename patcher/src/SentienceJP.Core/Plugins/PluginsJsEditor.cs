using System.Text.Json;

namespace SentienceJP.Core.Plugins;

/// <summary>plugins.js の形式エラー。利用者向けの日本語メッセージを持つ。</summary>
public sealed class PluginsJsFormatException : FormatException
{
    /// <summary>形式エラーを作る。</summary>
    /// <param name="message">エラーの説明。</param>
    public PluginsJsFormatException(string message)
        : base(message)
    {
    }

    /// <summary>原因となった例外を伴う形式エラーを作る。</summary>
    /// <param name="message">エラーの説明。</param>
    /// <param name="innerException">原因となった例外。</param>
    public PluginsJsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>plugins.js の配列要素 1 件の位置情報。</summary>
/// <param name="Start">要素の開始位置（テキスト全体での文字インデックス）。</param>
/// <param name="End">要素の終了位置（この位置の文字は含まない）。</param>
/// <param name="Name">要素の "name" プロパティ（無ければ null）。</param>
public sealed record PluginsJsElement(int Start, int End, string? Name);

/// <summary>
/// 解析済みの plugins.js。
/// </summary>
/// <param name="Text">ファイル全体のテキスト。</param>
/// <param name="ArrayStart">配列の開き括弧 '[' の位置。</param>
/// <param name="ArrayEnd">配列の閉じ括弧 ']' の位置。</param>
/// <param name="Elements">配列の要素（出現順）。</param>
/// <param name="NewLine">ファイルで使われている改行コード。</param>
public sealed record PluginsJsDocument(string Text, int ArrayStart, int ArrayEnd, IReadOnlyList<PluginsJsElement> Elements, string NewLine)
{
    /// <summary>指定した名前のプラグインが登録されているかを判定する。</summary>
    /// <param name="pluginName">プラグイン名。</param>
    public bool Contains(string pluginName) =>
        Elements.Any(e => string.Equals(e.Name, pluginName, StringComparison.Ordinal));
}

/// <summary>
/// RPGツクールMV の plugins.js（"var $plugins =" に続く JSON 配列）を、元のバイトを保ったまま編集する純粋関数群。
/// 方針:
/// - 配列部分を JSON として検証し、要素の位置を文字単位で特定する。
/// - 挿入は「最後の要素の直後に ',' + 改行 + 新要素」を差し込むだけにし、他の文字には一切触れない。
/// - 除去は挿入の逆操作になる範囲を削除するため、挿入→除去でバイト一致に戻る。
/// </summary>
public static class PluginsJsEditor
{
    /// <summary>
    /// plugins.js のテキストを解析する。
    /// "var $plugins" の宣言は、コメントと文字列リテラルの中を読み飛ばして探し、コードとしての宣言がちょうど 1 つの場合だけ受け付ける（CA-06）。
    /// </summary>
    /// <param name="text">plugins.js の内容。</param>
    /// <exception cref="PluginsJsFormatException">想定した形式でない場合（宣言が無い・2 つ以上ある・配列でない等）。</exception>
    public static PluginsJsDocument Parse(string text)
    {
        var declarations = PluginsJsDeclarationScanner.FindDeclarations(text);
        if (declarations.Count == 0)
        {
            throw new PluginsJsFormatException("plugins.js に \"var $plugins\" の宣言が見つかりません。");
        }

        if (declarations.Count > 1)
        {
            // どちらがゲームの使うプラグインの一覧か判断できないため、編集しない（取り違えるとスクリプトを壊す）
            throw new PluginsJsFormatException($"plugins.js に \"var $plugins\" の宣言が {declarations.Count} 個あります。どれがプラグインの一覧か判断できないため、編集しません。");
        }

        // 宣言の名前と '='、'=' と '[' の間には、空白とコメントだけを許す
        var equalsIndex = PluginsJsDeclarationScanner.SkipTrivia(text, declarations[0].NameEnd);
        if (equalsIndex >= text.Length || text[equalsIndex] != '=')
        {
            throw new PluginsJsFormatException("plugins.js の $plugins 宣言に '=' がありません。");
        }

        var arrayStart = PluginsJsDeclarationScanner.SkipTrivia(text, equalsIndex + 1);
        if (arrayStart >= text.Length || text[arrayStart] != '[')
        {
            throw new PluginsJsFormatException("plugins.js の $plugins が配列で始まっていません。");
        }

        var (elementSpans, arrayEnd) = ScanArray(text, arrayStart);
        var names = ReadElementNames(text[arrayStart..(arrayEnd + 1)], elementSpans.Count);
        var elements = elementSpans
            .Select((span, index) => new PluginsJsElement(span.Start, span.End, names[index]))
            .ToList();
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new PluginsJsDocument(text, arrayStart, arrayEnd, elements, newLine);
    }

    /// <summary>
    /// 配列の末尾にプラグインの要素を追加したテキストを返す（元のテキストは変更しない）。
    /// </summary>
    /// <param name="text">plugins.js の内容。</param>
    /// <param name="entryJson">追加する要素の JSON（1 行の圧縮形式）。</param>
    /// <exception cref="PluginsJsFormatException">形式が不正、要素が不正、または同名のプラグインが既に登録されている場合。</exception>
    public static string InsertEntry(string text, string entryJson)
    {
        var entryName = ReadEntryName(entryJson);
        var document = Parse(text);
        if (document.Contains(entryName))
        {
            throw new PluginsJsFormatException($"plugins.js には既に \"{entryName}\" が登録されています。");
        }

        // 要素が無い配列では '[' の直後に、ある場合は最後の要素の直後に差し込む
        var (position, insertion) = document.Elements.Count == 0
            ? (document.ArrayStart + 1, document.NewLine + entryJson)
            : (document.Elements[^1].End, "," + document.NewLine + entryJson);

        var result = text.Insert(position, insertion);

        // 編集結果が正しく解析でき、追加した要素が末尾にあることを確認する（自己検証）
        var verified = Parse(result);
        if (verified.Elements.Count == 0 || !string.Equals(verified.Elements[^1].Name, entryName, StringComparison.Ordinal))
        {
            throw new PluginsJsFormatException("plugins.js への追加結果の検証に失敗しました。");
        }

        return result;
    }

    /// <summary>
    /// 指定した名前のプラグイン要素をすべて取り除いたテキストを返す。
    /// 該当が無ければ元のテキストをそのまま返し、Changed は false になる。
    /// </summary>
    /// <param name="text">plugins.js の内容。</param>
    /// <param name="pluginName">取り除くプラグイン名。</param>
    /// <exception cref="PluginsJsFormatException">形式が不正な場合。</exception>
    public static (string Text, bool Changed) RemoveEntry(string text, string pluginName)
    {
        var current = text;
        var changed = false;

        // 同名の要素が複数ある異常系にも対応するため、無くなるまで 1 件ずつ取り除く
        while (true)
        {
            var document = Parse(current);
            var index = IndexOf(document, pluginName);
            if (index < 0)
            {
                return (current, changed);
            }

            var (start, end) = RemovalRange(document, index);
            current = current.Remove(start, end - start);
            changed = true;
        }
    }

    /// <summary>
    /// 要素を取り除く範囲を求める。挿入時に加えた区切り（',' と改行）ごと削除するため:
    /// - 先頭以外: 直前の要素の終端から、対象要素の終端まで（", 改行, 要素" を削除）
    /// - 先頭で後続あり: 対象要素の先頭から、次の要素の先頭まで（"要素, 改行" を削除）
    /// - 唯一の要素: '[' の直後から対象要素の終端まで（挿入時の "改行 + 要素" を削除）
    /// </summary>
    private static (int Start, int End) RemovalRange(PluginsJsDocument document, int index)
    {
        var elements = document.Elements;
        if (index > 0)
        {
            return (elements[index - 1].End, elements[index].End);
        }

        if (elements.Count > 1)
        {
            return (elements[0].Start, elements[1].Start);
        }

        return (document.ArrayStart + 1, elements[0].End);
    }

    /// <summary>指定した名前の要素の位置（無ければ -1）。</summary>
    private static int IndexOf(PluginsJsDocument document, string pluginName)
    {
        for (var i = 0; i < document.Elements.Count; i++)
        {
            if (string.Equals(document.Elements[i].Name, pluginName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 配列の開き括弧から走査し、トップレベルの要素の範囲と、閉じ括弧の位置を求める。
    /// 文字列リテラル内の括弧やエスケープされた引用符を正しく読み飛ばす。
    /// </summary>
    private static (List<(int Start, int End)> Elements, int ArrayEnd) ScanArray(string text, int arrayStart)
    {
        var elements = new List<(int Start, int End)>();
        var index = arrayStart + 1;
        var expectValue = true;

        while (true)
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length)
            {
                throw new PluginsJsFormatException("plugins.js の $plugins 配列が閉じられていません。");
            }

            var c = text[index];
            if (c == ']')
            {
                return (elements, index);
            }

            if (c == ',')
            {
                if (expectValue)
                {
                    throw new PluginsJsFormatException($"plugins.js の $plugins 配列に余分な ',' があります（{index} 文字目）。");
                }

                expectValue = true;
                index++;
                continue;
            }

            if (!expectValue)
            {
                throw new PluginsJsFormatException($"plugins.js の $plugins 配列で ',' が抜けています（{index} 文字目）。");
            }

            var end = ScanValue(text, index);
            elements.Add((index, end));
            index = end;
            expectValue = false;
        }
    }

    /// <summary>
    /// 1 つの JSON 値の終端位置（その値の直後の位置）を求める。
    /// オブジェクト・配列は入れ子を数え、文字列はエスケープを考慮して読み飛ばす。
    /// </summary>
    private static int ScanValue(string text, int start)
    {
        var c = text[start];
        if (c == '"')
        {
            return ScanString(text, start);
        }

        if (c is '{' or '[')
        {
            var depth = 0;
            var index = start;
            while (index < text.Length)
            {
                var current = text[index];
                if (current == '"')
                {
                    index = ScanString(text, index);
                    continue;
                }

                if (current is '{' or '[')
                {
                    depth++;
                }
                else if (current is '}' or ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index + 1;
                    }
                }

                index++;
            }

            throw new PluginsJsFormatException("plugins.js の $plugins 配列内のオブジェクトが閉じられていません。");
        }

        // 数値・true/false/null などのプリミティブ値は、区切りか空白の手前まで
        var position = start;
        while (position < text.Length && text[position] is not (',' or ']') && !char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        return position;
    }

    /// <summary>文字列リテラルの終端（閉じ引用符の直後の位置）を求める。</summary>
    private static int ScanString(string text, int start)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '\\')
            {
                index += 2;
                continue;
            }

            if (c == '"')
            {
                return index + 1;
            }

            index++;
        }

        throw new PluginsJsFormatException("plugins.js の $plugins 配列内の文字列が閉じられていません。");
    }

    /// <summary>空白をスキップした位置を返す。</summary>
    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// 配列部分を JSON として検証し、各要素の "name" を読み取る。
    /// 走査で求めた要素数と JSON としての要素数が一致しない場合は形式エラーとする。
    /// </summary>
    private static List<string?> ReadElementNames(string arrayText, int expectedCount)
    {
        try
        {
            using var document = JsonDocument.Parse(arrayText);
            var names = document.RootElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                        ? name.GetString()
                        : null)
                .ToList();

            if (names.Count != expectedCount)
            {
                throw new PluginsJsFormatException($"plugins.js の $plugins 配列の解析結果が一致しません（走査 {expectedCount} 件、JSON {names.Count} 件）。");
            }

            return names;
        }
        catch (JsonException ex)
        {
            throw new PluginsJsFormatException($"plugins.js の $plugins 配列が JSON として不正です: {ex.Message}", ex);
        }
    }

    /// <summary>追加する要素の JSON を検証し、"name" を返す。</summary>
    private static string ReadEntryName(string entryJson)
    {
        try
        {
            using var document = JsonDocument.Parse(entryJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(name.GetString()))
            {
                return name.GetString()!;
            }

            throw new PluginsJsFormatException("追加する plugins.js の要素に \"name\" がありません。");
        }
        catch (JsonException ex)
        {
            throw new PluginsJsFormatException($"追加する plugins.js の要素が JSON として不正です: {ex.Message}", ex);
        }
    }
}
