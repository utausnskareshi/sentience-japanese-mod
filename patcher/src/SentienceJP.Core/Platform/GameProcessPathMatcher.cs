namespace SentienceJP.Core.Platform;

/// <summary>
/// プロセスの実行ファイルが、ゲームフォルダ配下のものかを判定する純粋関数群（CA-09）。
/// ゲームフォルダやプロセスの実行ファイルがジャンクション・SUBST・8.3 形式の短い名前など別の表記で示されていても判定できるよう、
/// 表記どおりの比較に加えて、実体のパス（GetFinalPathNameByHandle で得たもの）同士でも比較する。
/// 実体のパスの取得（副作用のある OS 呼び出し）は引数の関数に任せ、ここでは判定だけを行う（単体テストで確かめられるようにするため）。
/// </summary>
internal static class GameProcessPathMatcher
{
    /// <summary>
    /// 1 つのプロセスについて、ゲームの起動状況を判定する。誤って「起動中」としないよう、次の順で判定する:
    /// 1. 表記どおりの比較（従来の判定）で配下なら「起動中」。
    /// 2. ゲームフォルダの実体が分からない（正規化に失敗した）場合は、従来の判定のまま「起動していない」。
    /// 3. プロセスの実行ファイルの実体が分からない場合は「判定不能」（利用者に確認を求める安全側）。
    /// 4. 実体のパス同士の比較で配下なら「起動中」、そうでなければ「起動していない」。
    /// </summary>
    /// <param name="imagePath">プロセスの実行ファイルのパス（QueryFullProcessImageName の結果）。</param>
    /// <param name="gameDirectory">ゲームのルートフォルダ（表記どおり）。</param>
    /// <param name="finalGameDirectory">ゲームのルートフォルダの実体のパス（取得できなかった場合は null）。</param>
    /// <param name="resolveFinalPath">パスから実体のパスを得る関数（取得できなければ null を返す）。</param>
    public static GameProcessState Classify(
        string imagePath,
        string gameDirectory,
        string? finalGameDirectory,
        Func<string, string?> resolveFinalPath)
    {
        if (IsUnder(imagePath, gameDirectory))
        {
            return GameProcessState.Running;
        }

        if (finalGameDirectory is null)
        {
            return GameProcessState.NotRunning;
        }

        var finalImagePath = resolveFinalPath(imagePath);
        if (finalImagePath is null)
        {
            return GameProcessState.Unknown;
        }

        return IsUnder(finalImagePath, finalGameDirectory) ? GameProcessState.Running : GameProcessState.NotRunning;
    }

    /// <summary>
    /// GetFinalPathNameByHandle が返す拡張パスの接頭辞を外し、通常の表記にする純粋関数。
    /// "\\?\C:\x" → "C:\x"、"\\?\UNC\server\share\x" → "\\server\share\x"。接頭辞が無ければそのまま返す。
    /// </summary>
    /// <param name="path">GetFinalPathNameByHandle の結果。</param>
    public static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.Ordinal) ? path[extendedPrefix.Length..] : path;
    }

    /// <summary>
    /// <paramref name="path"/> が <paramref name="directory"/> の配下かを判定する。
    /// パスとして解釈できない表記（不正な文字など）は「配下ではない」とする（例外で判定全体を止めない）。
    /// </summary>
    private static bool IsUnder(string path, string directory)
    {
        try
        {
            return GamePaths.IsUnderDirectory(path, directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
