namespace SentienceJP.Core.Steam;

/// <summary>
/// libraryfolders.vdf からライブラリのパス一覧を取り出す純粋関数。
/// 新形式（"0" { "path" "..." }）と旧形式（"1" "D:\\SteamLibrary"）の両方に対応する。
/// 各ライブラリの "apps" 一覧は更新が遅れることがあるため参照しない
/// （実際にこの PC でも対象ゲームが一覧に反映されていなかった）。
/// </summary>
public static class SteamLibraryFolders
{
    /// <summary>libraryfolders.vdf の内容からライブラリのパスを出現順に返す。</summary>
    /// <param name="vdfText">libraryfolders.vdf の内容。</param>
    /// <exception cref="KeyValuesParseException">構文が不正、または libraryfolders ブロックが無い場合。</exception>
    public static IReadOnlyList<string> ParseLibraryPaths(string vdfText)
    {
        var root = KeyValuesParser.Parse(vdfText);
        var folders = root.GetObject("libraryfolders")
            ?? throw new KeyValuesParseException("libraryfolders.vdf に \"libraryfolders\" ブロックがありません。");

        return folders.Children
            .Where(child => IsLibraryIndexKey(child.Key))
            .Select(child => child.Value switch
            {
                // 新形式: 番号ごとのブロックに "path" がある
                KvObject block => block.GetString("path"),

                // 旧形式: 番号の値がそのままパス
                KvString path => path.Value,
                _ => null,
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
    }

    /// <summary>ライブラリ番号のキー（"0"、"1" など数字のみ）かを判定する。</summary>
    private static bool IsLibraryIndexKey(string key) => key.Length > 0 && key.All(char.IsAsciiDigit);
}
