namespace SentienceJP.Core.Platform;

/// <summary>
/// パッチツール（exe）のコマンドライン引数。通常の利用者は引数なしで起動する。
/// </summary>
/// <param name="VerifyPayload">画面を出さずに埋め込みペイロードを自己検査し、終了コードで結果を返すか（ビルドスクリプト用）。</param>
/// <param name="ReportPath">自己検査の結果を書き出すファイル（指定しなければ書き出さない）。</param>
/// <param name="ElevatedRestart">管理者としての再起動で起動されたか（前のインスタンスの終了を少し待つ）。</param>
public sealed record PatcherCommandLine(bool VerifyPayload, string? ReportPath, bool ElevatedRestart)
{
    /// <summary>自己検査モードの指定。直後に結果ファイルのパスを続けてもよい。</summary>
    public const string VerifyPayloadSwitch = "--verify-payload";

    /// <summary>管理者としての再起動であることを示す指定（ツール自身が付ける）。</summary>
    public const string ElevatedRestartSwitch = "--elevated-restart";

    /// <summary>
    /// 引数を解釈する純粋関数。知らない引数は無視する（利用者がショートカット等で付けても通常どおり起動するため）。
    /// </summary>
    /// <param name="args">コマンドライン引数（exe 名を含まない）。</param>
    public static PatcherCommandLine Parse(IReadOnlyList<string> args)
    {
        var verifyIndex = IndexOf(args, VerifyPayloadSwitch);
        var reportPath = verifyIndex >= 0
                         && verifyIndex + 1 < args.Count
                         && !args[verifyIndex + 1].StartsWith("--", StringComparison.Ordinal)
                         && !string.IsNullOrWhiteSpace(args[verifyIndex + 1])
            ? args[verifyIndex + 1]
            : null;
        return new PatcherCommandLine(verifyIndex >= 0, reportPath, IndexOf(args, ElevatedRestartSwitch) >= 0);
    }

    /// <summary>指定した引数の位置（大文字小文字は区別しない。無ければ -1）。</summary>
    private static int IndexOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
