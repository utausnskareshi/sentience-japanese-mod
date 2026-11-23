namespace SentienceJP.Core.Patching;

/// <summary>
/// バックアップフォルダ内のパスの組み立て（純粋関数）。
/// 構成: &lt;game&gt;/sentience_jp_backup/state.json と &lt;game&gt;/sentience_jp_backup/original/&lt;元の相対パス&gt;
/// </summary>
public static class BackupLayout
{
    /// <summary>バックアップフォルダの相対パス。</summary>
    public static string BackupDirectory => GameConstants.BackupDirectoryName;

    /// <summary>状態ファイルの相対パス。</summary>
    public static string StateFile => $"{GameConstants.BackupDirectoryName}/{GameConstants.StateFileName}";

    /// <summary>元ファイルのコピーを置く相対パス。</summary>
    /// <param name="originalRelativePath">元ファイルの相対パス。</param>
    public static string BackupPathFor(string originalRelativePath) =>
        $"{GameConstants.BackupDirectoryName}/{GameConstants.BackupOriginalDirectoryName}/{originalRelativePath}";
}
