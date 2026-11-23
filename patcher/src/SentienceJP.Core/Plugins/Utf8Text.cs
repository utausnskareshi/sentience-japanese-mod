using System.Text;

namespace SentienceJP.Core.Plugins;

/// <summary>
/// UTF-8 テキストファイルの、バイト単位で可逆なデコード・エンコード。
/// plugins.js を編集しても、編集箇所以外のバイトが 1 バイトも変わらないことを保証するために使う。
/// </summary>
public static class Utf8Text
{
    /// <summary>UTF-8 の BOM。</summary>
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>不正なバイト列を置換せず例外にする UTF-8 エンコーディング。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// バイト列を文字列に変換する。BOM の有無を返し、再エンコードで元のバイト列に戻ることを確認する。
    /// </summary>
    /// <param name="bytes">ファイルの内容。</param>
    /// <param name="fileDescription">エラーメッセージに使うファイルの説明。</param>
    /// <exception cref="PatchException">UTF-8 として不正、または可逆に変換できない場合。</exception>
    public static (string Text, bool HasBom) Decode(byte[] bytes, string fileDescription)
    {
        var hasBom = bytes.AsSpan().StartsWith(Bom);
        var body = hasBom ? bytes.AsSpan(Bom.Length) : bytes.AsSpan();
        string text;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException ex)
        {
            throw new PatchException($"{fileDescription} が UTF-8 として読み取れません。ファイルが破損している可能性があります。", ex);
        }

        // 念のため、再エンコードで完全に元へ戻ることを確認してから編集に使う
        if (!Encode(text, hasBom).AsSpan().SequenceEqual(bytes))
        {
            throw new PatchException($"{fileDescription} を安全に編集できない文字コード形式です。");
        }

        return (text, hasBom);
    }

    /// <summary>文字列を UTF-8 のバイト列に変換する（元ファイルに BOM があった場合は付ける）。</summary>
    /// <param name="text">変換する文字列。</param>
    /// <param name="withBom">BOM を付けるか。</param>
    public static byte[] Encode(string text, bool withBom)
    {
        var body = StrictUtf8.GetBytes(text);
        return withBom ? [.. Bom, .. body] : body;
    }
}
