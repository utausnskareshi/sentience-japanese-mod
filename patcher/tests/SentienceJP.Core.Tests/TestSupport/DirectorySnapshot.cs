using SentienceJP.Core.Patching;

namespace SentienceJP.Core.Tests.TestSupport;

/// <summary>
/// フォルダ内の全ファイル（SHA-256 と属性）と全フォルダ（属性）を記録し、適用→復元の前後で「完全に元通り」かを比較する補助。
/// 属性は、パッチが保つべき範囲（読み取り専用・隠し・システム・アーカイブ・インデックス対象外）を比較する。
/// </summary>
internal static class DirectorySnapshot
{
    /// <summary>フォルダの印（ファイルのハッシュと区別するための値）。</summary>
    private const string DirectoryMarker = "<dir>";

    /// <summary>相対パス（"/" 区切り）→ ハッシュ（フォルダは印）と属性 の辞書を作る。</summary>
    /// <param name="root">対象のフォルダ。</param>
    public static SortedDictionary<string, string> Capture(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            result[Relative(root, directory)] = $"{DirectoryMarker} [{Attributes(directory)}]";
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result[Relative(root, file)] = $"{Hashing.Sha256HexOfFile(file)} [{Attributes(file)}]";
        }

        return result;
    }

    /// <summary>2 つのスナップショットの差分を、読みやすい文字列の一覧にする（空なら一致）。</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        before.Keys.Union(after.Keys)
            .Where(key => !before.TryGetValue(key, out var b) || !after.TryGetValue(key, out var a) || !string.Equals(a, b, StringComparison.Ordinal))
            .Select(key => $"{key}: {(before.TryGetValue(key, out var b) ? b : "(なし)")} → {(after.TryGetValue(key, out var a) ? a : "(なし)")}")
            .ToList();

    /// <summary>ルートからの相対パスを "/" 区切りで返す。</summary>
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>比較対象の属性を文字列にする。</summary>
    private static string Attributes(string path) => (File.GetAttributes(path) & FileOperations.PreservedAttributes).ToString();
}
