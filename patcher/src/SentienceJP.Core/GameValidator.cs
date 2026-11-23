namespace SentienceJP.Core;

/// <summary>
/// ゲームフォルダの妥当性確認。
/// </summary>
public static class GameValidator
{
    /// <summary>
    /// 必須ファイル（<see cref="GameConstants.RequiredGameFiles"/>）のうち、存在しないものの相対パスを返す。
    /// 空のリストなら有効なゲームフォルダ。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    public static IReadOnlyList<string> FindMissingFiles(string gameDirectory) =>
        GameConstants.RequiredGameFiles
            .Where(relativePath => !File.Exists(GamePaths.ToFullPath(gameDirectory, relativePath)))
            .ToList();

    /// <summary>
    /// 起動時に検出したゲームフォルダが、その後に無くなっていないか（Steam でのアンインストール・ライブラリの移動・外付けドライブの取り外し）を調べる（AA-08）。
    /// 無くなっていればその説明を、あれば null を返す。読み取りのみで、何も変更しない。
    /// 「無い」とみなすのは、フォルダ自体が無い場合と、必須ファイル（<see cref="GameConstants.RequiredGameFiles"/>）が 1 つも無い場合
    /// （アンインストール後に、Steam が消さないパッチのファイルだけが残った状態）。plugins.js だけが無い場合は該当しない（CA-05 で扱う）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    public static string? DescribeMissingGame(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory))
        {
            return $"ゲームフォルダがありません: {gameDirectory}";
        }

        return FindMissingFiles(gameDirectory).Count == GameConstants.RequiredGameFiles.Count
            ? $"ゲームフォルダに、ゲームのファイル（{string.Join("・", GameConstants.RequiredGameFiles)}）がありません: {gameDirectory}"
            : null;
    }
}
