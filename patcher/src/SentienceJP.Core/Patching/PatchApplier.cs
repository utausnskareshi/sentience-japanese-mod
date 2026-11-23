using SentienceJP.Core.Payload;
using SentienceJP.Core.Plugins;

namespace SentienceJP.Core.Patching;

/// <summary>
/// パッチの適用処理（トランザクション）。
/// 手順: 事前検証 → plugins.js の編集内容を作成（純粋関数）→ バックアップ → ファイル追加 → plugins.js 置換 → 状態ファイル記録 → 検証。
/// 途中で失敗した場合は、それまでに行った変更を逆順に取り消して、適用前の状態に戻す。
/// 取り消しの一部に失敗した場合は、後から「元に戻す」で片付けられるよう、バックアップフォルダ（状態ファイルを含む）を残す。
/// </summary>
public static class PatchApplier
{
    /// <summary>取り消し操作。</summary>
    /// <param name="Description">説明（失敗時のメッセージに使う）。</param>
    /// <param name="Action">取り消しの処理。</param>
    /// <param name="KeepIfEarlierUndoFailed">
    /// 先に実行した取り消しが 1 つでも失敗していたら、この取り消しを行わない（バックアップフォルダの削除に使う。
    /// 戻しきれなかった変更を、後から「元に戻す」でバックアップを使って片付けられるようにするため）。
    /// </param>
    private sealed record UndoStep(string Description, Action Action, bool KeepIfEarlierUndoFailed = false);

    /// <summary>
    /// パッチを適用する。事前条件（未適用の状態であること等）を満たさない場合も失敗の結果を返す。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="gameBuildId">ゲームのビルドID（状態ファイルへの記録用。不明なら null）。</param>
    /// <param name="payload">検証済みのペイロード。</param>
    /// <param name="log">進行状況の通知先。</param>
    /// <param name="timeProvider">適用日時の取得元（テスト用に差し替え可能。省略時はシステム時刻）。</param>
    public static PatchOperationResult Apply(
        string gameDirectory,
        string? gameBuildId,
        PayloadBundle payload,
        Action<string> log,
        TimeProvider? timeProvider = null) =>
        Apply(gameDirectory, gameBuildId, payload, log, timeProvider ?? TimeProvider.System, FileOperations.Default);

    /// <summary>
    /// パッチを適用する（ファイル操作を差し替え可能な版。テストで各段階の失敗を注入するために使う）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="gameBuildId">ゲームのビルドID。</param>
    /// <param name="payload">検証済みのペイロード。</param>
    /// <param name="log">進行状況の通知先。</param>
    /// <param name="clock">適用日時の取得元。</param>
    /// <param name="fileSystem">ファイル操作。</param>
    internal static PatchOperationResult Apply(
        string gameDirectory,
        string? gameBuildId,
        PayloadBundle payload,
        Action<string> log,
        TimeProvider clock,
        IPatchFileSystem fileSystem)
    {
        var undo = new Stack<UndoStep>();
        try
        {
            ApplyCore(gameDirectory, gameBuildId, payload, log, clock, fileSystem, undo);
            log("日本語化パッチの適用が完了しました。");
            return PatchOperationResult.Succeeded("日本語化が完了しました。");
        }
        catch (Exception ex)
        {
            // どんな例外でも必ずロールバックし、原因は結果として呼び出し元へ返す（握りつぶさない）。
            // ログ欄にも出るため、よくある例外は日本語の説明にする（英語の原文は括弧書きで残る。AA-13）
            log($"エラーが発生しました: {ErrorDescriber.Describe(ex)}");
            var hadChanges = undo.Count > 0;
            var cleanupErrors = Rollback(undo, log);
            return PatchOperationResult.Failed(DescribeFailure(ex, hadChanges, cleanupErrors), ex, cleanupErrors);
        }
    }

    /// <summary>
    /// 失敗時に利用者へ示す文言を組み立てる純粋関数。
    /// 取り消しの成否で文言を分け、「元に戻した」と誤って伝えないようにする。
    /// </summary>
    /// <param name="error">失敗の原因。</param>
    /// <param name="rolledBack">取り消すべき変更があったか（ファイルを書き換えた後の失敗か）。</param>
    /// <param name="cleanupErrors">取り消しで発生した失敗。</param>
    internal static string DescribeFailure(Exception error, bool rolledBack, IReadOnlyList<string> cleanupErrors)
    {
        var newLine = Environment.NewLine;

        // よくある例外（アクセス拒否・使用中・容量不足など）は日本語の説明にし、英語の原文は括弧書きで残す（AA-13）
        var cause = error is PatchException ? error.Message : $"原因: {ErrorDescriber.Describe(error)}";
        if (cleanupErrors.Count > 0)
        {
            return $"日本語化の途中でエラーが発生し、変更の一部を取り消せませんでした。{newLine}"
                + $"バックアップは残してあるので、原因を取り除いてから「元に戻す」を実行してください。{newLine}{cause}";
        }

        if (!rolledBack)
        {
            // まだ何も書き換えていない段階の失敗（事前検証など）
            return error is PatchException ? error.Message : $"日本語化を開始できませんでした。{newLine}{cause}";
        }

        return error is PatchException
            ? $"{error.Message}{newLine}（変更はすべて取り消し、元の状態に戻しました）"
            : $"日本語化の途中でエラーが発生したため、元の状態に戻しました。{newLine}{cause}";
    }

    /// <summary>適用の本体。各変更の直後に、その取り消し操作を積む。</summary>
    private static void ApplyCore(
        string gameDirectory,
        string? gameBuildId,
        PayloadBundle payload,
        Action<string> log,
        TimeProvider clock,
        IPatchFileSystem fileSystem,
        Stack<UndoStep> undo)
    {
        var manifest = payload.Manifest;

        // 1. 事前検証: 未適用の状態でなければ適用しない
        log("ゲームフォルダの状態を確認しています…");
        var snapshot = GameSnapshotReader.Read(gameDirectory, manifest);
        var status = PatchStatusDetector.Detect(snapshot, manifest);
        if (status.Kind != PatchStatusKind.NotApplied)
        {
            // ゲームフォルダが無くなっていれば、何も書き込まずに起動し直すよう案内する（AA-08）
            if (snapshot.MissingGameReason is { } missingGame)
            {
                throw new PatchException($"{missingGame}{Environment.NewLine}{PatchMessages.GameNotFoundGuidance}");
            }

            // 新しいバージョンのツールで適用されている場合は、そのツールで元に戻すよう案内する（CA-04）
            if (snapshot.UnsupportedState is { } unsupported)
            {
                throw new PatchException(PatchMessages.DescribeApplyBlockedByNewerVersion(unsupported));
            }

            // plugins.js を使えず（無い・読めない・解析できない）、戻すためのバックアップの記録も無い場合は「元に戻す」では直らないため、
            // 理由に応じた対処方法を案内する（CA-05・CA-08）
            if (snapshot.PluginsJsError is { } pluginsError && snapshot.State is null && snapshot.EffectivePluginsJsProblem is { } problem)
            {
                throw new PatchException(PatchMessages.DescribeApplyBlockedByPluginsJs(problem, pluginsError));
            }

            throw new PatchException($"現在の状態では適用できません（{string.Join(" ", status.Reasons)}）。先に「元に戻す」を実行してください。");
        }

        // パッチ専用の場所（www/sentience_jp・バックアップフォルダ・プラグインファイル）がリンクになっていれば、
        // リンク先（ゲームフォルダの外）へ書き込まないよう、最初の書き込みの前に中止する（CA-01）
        PatchLinkGuard.EnsureNoLinks(gameDirectory);

        // 追加先にファイルやフォルダが既に存在しないことを、書き込みを始める前に確認する（副作用の無い段階で失敗させる）
        var conflicts = manifest.Files
            .Select(f => f.Path)
            .Where(path =>
            {
                var full = GamePaths.ToFullPath(gameDirectory, path);
                return File.Exists(full) || Directory.Exists(full);
            })
            .ToList();
        if (conflicts.Count > 0)
        {
            throw new PatchException($"追加先に既にファイルがあります: {string.Join(", ", conflicts)}");
        }

        // バックアップフォルダは、この適用で新しく作ったものだけを取り消し時に削除する。
        // 既にある（同時に動いている別の処理が作った等）場合は、他者のバックアップを消さないよう書き込み前に中止する。
        var backupDirectoryFullPath = GamePaths.ToFullPath(gameDirectory, BackupLayout.BackupDirectory);
        if (Directory.Exists(backupDirectoryFullPath) || File.Exists(backupDirectoryFullPath))
        {
            throw new PatchException($"バックアップフォルダ（{GameConstants.BackupDirectoryName}）が既にあります。先に「元に戻す」を実行してください。");
        }

        // 2. plugins.js の編集内容を作る（ここまでは読み取りと計算のみ）
        var pluginsFullPath = GamePaths.ToFullPath(gameDirectory, GameConstants.PluginsJsRelativePath);
        var originalBytes = fileSystem.ReadAllBytes(pluginsFullPath);
        var (originalText, hasBom) = Utf8Text.Decode(originalBytes, "plugins.js");
        var patchedText = PluginsJsFormat.Guard(() => PluginsJsEditor.InsertEntry(originalText, manifest.PluginEntryJson));
        var patchedBytes = Utf8Text.Encode(patchedText, hasBom);
        var originalSha = Hashing.Sha256Hex(originalBytes);
        var patchedSha = Hashing.Sha256Hex(patchedBytes);

        // 3. バックアップ（plugins.js の元ファイルをコピー）
        log("plugins.js をバックアップしています…");
        var backupRelativePath = BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath);
        fileSystem.CreateDirectory(backupDirectoryFullPath);
        undo.Push(new UndoStep(
            "バックアップフォルダの削除",
            () => fileSystem.DeleteDirectoryRecursive(backupDirectoryFullPath),
            KeepIfEarlierUndoFailed: true));

        var backupFullPath = GamePaths.ToFullPath(gameDirectory, backupRelativePath);
        fileSystem.CreateDirectory(Path.GetDirectoryName(backupFullPath)!);
        fileSystem.WriteNewFile(backupFullPath, originalBytes);
        if (!string.Equals(Hashing.Sha256Hex(fileSystem.ReadAllBytes(backupFullPath)), originalSha, StringComparison.Ordinal))
        {
            throw new PatchException("plugins.js のバックアップを正しく作成できませんでした。");
        }

        // 4. ペイロードのファイルを追加（新規に作ったフォルダも記録しておき、復元時に削除できるようにする）
        var createdDirectories = new List<string>();
        foreach (var entry in manifest.Files)
        {
            log($"ファイルを追加しています: {entry.Path}");
            foreach (var directory in GamePaths.AncestorDirectories(entry.Path))
            {
                var directoryFullPath = GamePaths.ToFullPath(gameDirectory, directory);
                if (!Directory.Exists(directoryFullPath))
                {
                    fileSystem.CreateDirectory(directoryFullPath);
                    createdDirectories.Add(directory);
                    undo.Push(new UndoStep($"フォルダの削除: {directory}", () => fileSystem.DeleteDirectoryIfEmpty(directoryFullPath)));
                }
            }

            // 書き込みの途中で失敗しても中途半端なファイルが残らないよう、取り消し操作を先に積んでから書く
            var fileFullPath = GamePaths.ToFullPath(gameDirectory, entry.Path);
            undo.Push(new UndoStep($"追加ファイルの削除: {entry.Path}", () => fileSystem.DeleteFile(fileFullPath)));
            fileSystem.WriteNewFile(fileFullPath, payload.Files[entry.Path]);
        }

        // 5. plugins.js を置き換え（一時ファイル経由）。
        // 取り消しは無条件に書き戻さず、その時点の内容を見て戻し方を決める（適用中に Steam の更新等で置き換わった内容を消さない。
        // 適用前の内容のままなら何もしない）。そのため、置き換えの途中で失敗した場合にも備えて、取り消し操作は置き換えの前に積む。
        log("plugins.js にプラグインを登録しています…");
        undo.Push(new UndoStep(
            "plugins.js を元に戻す",
            () => RevertPluginsJs(fileSystem, pluginsFullPath, originalBytes, originalSha, patchedSha, log)));
        fileSystem.ReplaceFile(pluginsFullPath, patchedBytes);

        // 6. 状態ファイルを記録（一時ファイル経由で書き、中断しても書きかけの状態ファイルを残さない）
        var state = new PatchStateFile(
            GameConstants.StateSchemaVersion,
            manifest.PatchVersion,
            manifest.TranslationVersion,
            clock.GetUtcNow(),
            gameBuildId,
            manifest.Files.Select(f => new AddedFileRecord(f.Path, f.Sha256)).ToList(),
            [new ModifiedFileRecord(GameConstants.PluginsJsRelativePath, originalSha, patchedSha, backupRelativePath)],
            createdDirectories);
        var stateFullPath = GamePaths.ToFullPath(gameDirectory, BackupLayout.StateFile);
        fileSystem.ReplaceFile(stateFullPath, PatchStateSerializer.Serialize(state));

        // 7. 検証: 改めて読み取って「適用済み」と判定されることを確認する
        log("適用結果を検証しています…");
        var after = PatchStatusDetector.Detect(GameSnapshotReader.Read(gameDirectory, manifest), manifest);
        if (after.Kind != PatchStatusKind.Applied)
        {
            throw new PatchException($"適用後の検証に失敗しました（{string.Join(" ", after.Reasons)}）。");
        }
    }

    /// <summary>
    /// 適用の取り消しとして plugins.js を戻す。復元と同じ規則（<see cref="PluginsJsRevertPlanner"/>）で、
    /// 現在の内容が適用直後のままなら元の内容に、別の内容に変わっていればエントリだけを取り除き、エントリが無ければ触らない。
    /// 適用中に plugins.js が消えた場合は、手元の適用前の内容で作り直す（CA-05。読めないだけの場合は例外のまま取り消しの失敗として扱う）。
    /// </summary>
    private static void RevertPluginsJs(
        IPatchFileSystem fileSystem,
        string pluginsFullPath,
        byte[] originalBytes,
        string originalSha,
        string patchedSha,
        Action<string> log)
    {
        var currentBytes = PluginsJsReverter.ReadIfExists(fileSystem, pluginsFullPath);
        var decision = PluginsJsRevertPlanner.Decide(
            currentBytes is null ? null : PluginsJsObservation.Observe(currentBytes),
            originalSha,
            patchedSha,
            originalSha);
        PluginsJsReverter.Execute(fileSystem, pluginsFullPath, decision, currentBytes, originalBytes, log);
    }

    /// <summary>
    /// 積まれた取り消し操作を逆順に実行する。個々の失敗は記録して続行し、最後にまとめて返す。
    /// 先に失敗があった場合は、バックアップフォルダの削除（<see cref="UndoStep.KeepIfEarlierUndoFailed"/>）を行わずに残す。
    /// </summary>
    private static List<string> Rollback(Stack<UndoStep> undo, Action<string> log)
    {
        var errors = new List<string>();
        if (undo.Count > 0)
        {
            log("変更を取り消しています…");
        }

        while (undo.TryPop(out var step))
        {
            if (step.KeepIfEarlierUndoFailed && errors.Count > 0)
            {
                log($"取り消せなかった変更があるため、{step.Description}を行わずにバックアップを残します（後から「元に戻す」で使います）。");
                continue;
            }

            try
            {
                step.Action();
            }
            catch (Exception ex)
            {
                // 1 つの取り消しに失敗しても残りの取り消しは続ける。失敗内容は記録して呼び出し元へ返す（画面に出るため日本語の説明にする。AA-13）
                var message = $"{step.Description} に失敗しました: {ErrorDescriber.Describe(ex)}";
                errors.Add(message);
                log(message);
            }
        }

        if (errors.Count > 0)
        {
            log("一部の変更を取り消せませんでした。原因を取り除いてから「元に戻す」を実行してください。うまくいかない場合は Steam の「ゲームファイルの整合性を確認」を実行してください。");
        }

        return errors;
    }
}
