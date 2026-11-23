using System.Diagnostics;

namespace SentienceJP.Core.Tests.TestSupport;

/// <summary>
/// テスト用のジャンクション（ディレクトリのリンク）を作り、破棄時にリンク自体だけを削除する補助。
/// ジャンクションは mklink /J で作る（一般ユーザーの権限で作れる。シンボリックリンクは権限が必要なため使わない）。
/// <see cref="TempDirectory"/> の破棄は配下を再帰的に列挙するため、リンク先を巻き込まないよう、必ずこちらを先に破棄すること。
/// </summary>
internal sealed class JunctionScope : IDisposable
{
    /// <summary>作ったジャンクション（作った順）。</summary>
    private readonly List<string> _links = [];

    /// <summary>ジャンクションを作る（リンクの場所は存在してはいけない。親フォルダは必要なら作る）。</summary>
    /// <param name="link">作るリンクのパス。</param>
    /// <param name="target">リンク先のフォルダ（存在すること）。</param>
    public void Create(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("cmd.exe を起動できません。");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !IsJunction(link))
        {
            throw new InvalidOperationException($"ジャンクションを作れませんでした（{link} → {target}）: {output}");
        }

        _links.Add(link);
    }

    /// <summary>パスがリパースポイント（ジャンクション等）のフォルダかを返す。</summary>
    /// <param name="path">調べるパス。</param>
    public static bool IsJunction(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <inheritdoc />
    public void Dispose()
    {
        // 後から作ったものから順に、リンク自体だけを削除する（非再帰の削除はリンク先に触れない）
        foreach (var link in Enumerable.Reverse(_links))
        {
            if (IsJunction(link))
            {
                Directory.Delete(link);
            }
        }
    }
}
