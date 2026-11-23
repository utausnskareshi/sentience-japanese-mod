using System.Text.RegularExpressions;

namespace SentienceJP.Core;

/// <summary>
/// パッチのバージョン文字列（"x.y.z" と、任意のプレリリース・ビルドの接尾辞）の形式の検証（AA-15・CA-04）。
/// ビルドスクリプト（patcher/build.ps1）の検査と同じ形式にそろえる:
/// 先頭から末尾まで（末尾の改行も許さない）、ASCII の数字だけの 1〜4 個の数を "." でつなぎ、
/// 任意で "-" に続くプレリリース識別子、"+" に続くビルド識別子（英数字・"."・"-"）を付けたもの。
/// 例: "0.1.0"、"1.2.3-beta.1"、"1.0.0+abc"、"1.2.3.4"。全角数字・アラビア数字・末尾の改行や空白は受け付けない。
/// </summary>
public static partial class PatchVersionFormat
{
    /// <summary>
    /// 形式の正規表現（build.ps1 の -cnotmatch と同じ）。
    /// \A と \z で文字列全体に一致させ（$ は末尾の改行の前でも一致するため使わない）、\d ではなく [0-9] で ASCII の数字に限る。
    /// </summary>
    public const string Pattern = @"\A[0-9]+(\.[0-9]+){0,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\z";

    /// <summary>バージョン文字列が形式に合うかを判定する純粋関数。</summary>
    /// <param name="text">判定する文字列（null は不合格）。</param>
    public static bool IsValid(string? text) => text is not null && VersionRegex().IsMatch(text);

    /// <summary>形式の正規表現（ソース生成。文化に依存しない）。</summary>
    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
