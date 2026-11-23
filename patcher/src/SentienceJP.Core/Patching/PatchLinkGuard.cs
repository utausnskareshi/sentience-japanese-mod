namespace SentienceJP.Core.Patching;

/// <summary>
/// パッチ専用の場所にあるリパースポイント（ジャンクション・シンボリックリンク）の検出（CA-01）。
/// パッチ専用の場所（www/sentience_jp とその配下・sentience_jp_backup とその配下・プラグインファイル）がリンクになっていると、
/// 適用はリンク先（ゲームフォルダの外）へファイルを書き込み、元に戻すはリンク先のファイルや空フォルダを削除してしまう。
/// そのため、適用・復元とも最初の変更の前にリンクが無いことを確かめ、あれば何も変更せずに中止する。
/// ゲームルート・www・www/js・www/js/plugins がジャンクション（別ドライブへ移したゲーム・MOD 管理ツール等）という
/// 正当な構成は従来どおり扱えるよう、祖先のフォルダは検査しない（パッチ専用の場所だけを検査する）。
/// ここでは読み取り（属性の取得とフォルダの列挙）だけを行い、書き込みは一切しない。
/// </summary>
internal static class PatchLinkGuard
{
    /// <summary>それ自身と配下のすべて（ファイル・フォルダ）を検査するフォルダ（ゲームルートからの相対パス）。</summary>
    internal static readonly IReadOnlyList<string> GuardedDirectories =
    [
        GameConstants.PatchDataDirectoryRelativePath,
        GameConstants.BackupDirectoryName,
    ];

    /// <summary>それ自身を検査するファイル（ゲームルートからの相対パス）。</summary>
    internal static readonly IReadOnlyList<string> GuardedFiles =
    [
        GameConstants.PluginFileRelativePath,
    ];

    /// <summary>
    /// 子要素の列挙設定。リンク自体も列挙する（属性で除外しない）が、配下へは再帰しない
    /// （再帰は <see cref="Walk"/> が自分で行い、リンクの中には入らない）。列挙できないフォルダは例外にする（見落とさない）。
    /// </summary>
    private static readonly EnumerationOptions ChildrenIncludingLinks = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// パッチ専用の場所にあるリンクを、ゲームルートからの相対パス（"/" 区切り、序数順）で返す。無ければ空。
    /// リンクの中には入らない（リンク先のフォルダは列挙しない）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <exception cref="UnauthorizedAccessException">検査するフォルダを列挙できない場合（見落としを避けるため、そのまま伝える）。</exception>
    public static IReadOnlyList<string> FindLinks(string gameDirectory)
    {
        var found = new List<string>();
        foreach (var relative in GuardedFiles.Concat(GuardedDirectories))
        {
            var fullPath = GamePaths.ToFullPath(gameDirectory, relative);
            if (TryGetAttributes(fullPath) is not { } attributes)
            {
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                found.Add(relative);
            }
            else if (attributes.HasFlag(FileAttributes.Directory) && GuardedDirectories.Contains(relative, StringComparer.Ordinal))
            {
                foreach (var child in new DirectoryInfo(fullPath).EnumerateFileSystemInfos("*", ChildrenIncludingLinks))
                {
                    Walk(child, $"{relative}/{child.Name}", found);
                }
            }
        }

        return found.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// パッチ専用の場所にリンクがあれば、どこがリンクかと対処方法を示す <see cref="PatchException"/> を投げる。
    /// 適用・復元の最初の変更の前に呼ぶ（この時点では何も変更していないため、中止しても安全）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <exception cref="PatchException">リンクがある場合。</exception>
    public static void EnsureNoLinks(string gameDirectory)
    {
        var links = FindLinks(gameDirectory);
        if (links.Count > 0)
        {
            throw new PatchException(DescribeLinks(links));
        }
    }

    /// <summary>
    /// リンクが見つかったときの利用者向けの説明を作る純粋関数（どこがリンクか・何もしていないこと・リンクを外してから再実行すること）。
    /// </summary>
    /// <param name="links">リンクになっている場所（ゲームルートからの相対パス、1 件以上）。</param>
    internal static string DescribeLinks(IReadOnlyList<string> links)
    {
        var newLine = Environment.NewLine;
        return $"パッチ専用の場所が、リンク（ジャンクションまたはシンボリックリンク）になっています: {string.Join(", ", links)}{newLine}"
            + $"リンク先（ゲームフォルダの外の可能性があります）のファイルを書き換えたり削除したりしないよう、何も変更せずに中止しました。{newLine}"
            + "これらのリンクを外して（リンク自体を削除して）から、もう一度実行してください。";
    }

    /// <summary>
    /// 1 つの要素を調べ、リンクなら記録する。リンクでないフォルダなら、その子要素も同じように調べる（リンクの中には入らない）。
    /// </summary>
    private static void Walk(FileSystemInfo entry, string relativePath, List<string> found)
    {
        // 列挙で得た属性は、リンク先ではなくリンク自体の属性（FindFirstFile の結果）
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            found.Add(relativePath);
            return;
        }

        if (entry is DirectoryInfo directory)
        {
            foreach (var child in directory.EnumerateFileSystemInfos("*", ChildrenIncludingLinks))
            {
                Walk(child, $"{relativePath}/{child.Name}", found);
            }
        }
    }

    /// <summary>
    /// ファイル・フォルダの属性を返す（無ければ null）。最後の要素がリンクの場合はリンク先ではなくリンク自体の属性を返す。
    /// </summary>
    private static FileAttributes? TryGetAttributes(string fullPath)
    {
        try
        {
            return File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
