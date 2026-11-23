using System.Security.Cryptography;

namespace SentienceJP.Core;

/// <summary>
/// SHA-256 ハッシュの計算（16進小文字）。ペイロードの検証や、適用前後のファイル照合に使う。
/// </summary>
public static class Hashing
{
    /// <summary>バイト列の SHA-256 を 16 進小文字の文字列で返す。</summary>
    /// <param name="data">対象のバイト列。</param>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>ファイルの SHA-256 を 16 進小文字の文字列で返す。</summary>
    /// <param name="path">対象ファイルのパス。</param>
    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>文字列が SHA-256 の 16 進小文字表現（64 文字）として妥当かを判定する。</summary>
    /// <param name="value">判定する文字列。</param>
    public static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
