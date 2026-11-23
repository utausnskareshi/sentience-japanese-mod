namespace SentienceJP.Core.Platform;

/// <summary>書き込めなかった理由の種類。</summary>
public enum WriteAccessProblemKind
{
    /// <summary>アクセスが拒否された（権限不足、コントロールされたフォルダー アクセス、ウイルス対策ソフトなど）。</summary>
    AccessDenied,

    /// <summary>その他の入出力エラー（ディスクの空き容量不足、ファイルの使用中など）。権限では解決しない。</summary>
    IOError,
}

/// <summary>書き込めなかったフォルダと、その理由。</summary>
/// <param name="RelativeDirectory">ゲームルートからの相対パス（空文字はゲームルート）。</param>
/// <param name="Reason">失敗の理由（利用者向けの日本語。AA-13）。</param>
/// <param name="Kind">理由の種類（管理者としての再起動で解決しうるかの判断に使う）。</param>
/// <param name="Detail">
/// 例外の原文（英語。確認用の一時ファイルの名前などを含むため画面には出さず、ログに残す。情報を失わないため）。無ければ null。
/// </param>
public sealed record WriteAccessProblem(string RelativeDirectory, string Reason, WriteAccessProblemKind Kind, string? Detail = null);

/// <summary>書き込めなかったときの案内。</summary>
/// <param name="Message">利用者に示す文言。</param>
/// <param name="OfferRestartAsAdministrator">管理者としての再起動を提案するか（文言は再起動するかの質問で終わる）。</param>
public sealed record WriteAccessAdvice(string Message, bool OfferRestartAsAdministrator);

/// <summary>
/// ゲームフォルダへの書き込み権限を、実際に一時ファイルを作って確認する。
/// （ACL の解析より、実際に書けるかを試す方が確実なため）
/// </summary>
public static class WriteAccessChecker
{
    /// <summary>
    /// 指定したフォルダ群に書き込めるかを確認し、書き込めなかったものを返す。存在しないフォルダは確認しない。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="relativeDirectories">確認するフォルダ（ゲームルートからの相対パス）。</param>
    public static IReadOnlyList<WriteAccessProblem> FindProblems(string gameDirectory, IEnumerable<string> relativeDirectories) =>
        relativeDirectories
            .Select(relative => (Relative: relative, Full: GamePaths.ToFullPath(gameDirectory, relative)))
            .Where(pair => Directory.Exists(pair.Full))
            .Select(pair => TryWrite(pair.Full) is { } failure ? new WriteAccessProblem(pair.Relative, failure.Reason, failure.Kind, failure.Detail) : null)
            .Where(problem => problem is not null)
            .Select(problem => problem!)
            .ToList();

    /// <summary>
    /// 書き込めなかったときの案内を決める純粋関数。
    /// - アクセス拒否があり、管理者として実行していない: 管理者としての再起動を提案する。
    /// - アクセス拒否があり、既に管理者として実行している: 再起動しても解決しないため、他の原因の候補を示す。
    /// - アクセス拒否以外（容量不足・使用中など）: 権限の問題ではないことを伝え、確認すべき点を示す。
    /// </summary>
    /// <param name="problems">書き込めなかったフォルダ（1 件以上）。</param>
    /// <param name="isElevated">このツールが管理者として実行されているか。</param>
    public static WriteAccessAdvice Advise(IReadOnlyList<WriteAccessProblem> problems, bool isElevated)
    {
        var newLine = Environment.NewLine;
        var list = string.Join(
            newLine,
            problems.Select(p => $"・{(p.RelativeDirectory.Length == 0 ? "(ゲームフォルダ)" : p.RelativeDirectory)}: {p.Reason}"));

        if (problems.Any(p => p.Kind == WriteAccessProblemKind.AccessDenied))
        {
            if (!isElevated)
            {
                return new WriteAccessAdvice(
                    $"ゲームフォルダに書き込む権限がありません。{newLine}{list}{newLine}{newLine}管理者としてこのツールを再起動しますか？",
                    true);
            }

            return new WriteAccessAdvice(
                $"管理者として実行していますが、ゲームフォルダに書き込めません。{newLine}{list}{newLine}{newLine}"
                + $"次の原因が考えられます。設定を確認してから、もう一度実行してください。{newLine}"
                + $"・Windows セキュリティの「コントロールされたフォルダー アクセス」（ランサムウェア防止）が、このツールをブロックしている{newLine}"
                + $"・ウイルス対策ソフトが、ゲームフォルダへの書き込みをブロックしている{newLine}"
                + "・ゲームフォルダが、書き込みできないドライブ（読み取り専用のドライブやネットワーク上の場所）にある",
                false);
        }

        return new WriteAccessAdvice(
            $"ゲームフォルダに書き込めませんでした（権限の問題ではありません）。{newLine}{list}{newLine}{newLine}"
            + "ディスクの空き容量が足りているか、ゲームのファイルを他のプログラムが使用していないかを確認してから、もう一度実行してください。",
            false);
    }

    /// <summary>
    /// 一時ファイルを作って消す。失敗した場合はその理由（日本語）・種類・原文を返す（成功時は null）。
    /// 理由は日本語の説明だけにする（原文には確認用の一時ファイルの名前が入り、利用者には意味が無いため。原文は Detail としてログに残す。AA-13）。
    /// </summary>
    private static (string Reason, WriteAccessProblemKind Kind, string Detail)? TryWrite(string directory)
    {
        var path = Path.Combine(directory, $"sentience_jp_write_test_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return (ErrorDescriber.Explain(ex) ?? ex.Message, WriteAccessProblemKind.AccessDenied, ex.Message);
        }
        catch (IOException ex)
        {
            return (ErrorDescriber.Explain(ex) ?? ex.Message, WriteAccessProblemKind.IOError, ex.Message);
        }
    }
}
