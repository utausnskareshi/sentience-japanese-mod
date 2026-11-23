using SentienceJP.Core.Payload;

namespace SentienceJP.Core.Patching;

/// <summary>
/// パッチの復元（元に戻す）処理。
/// 手順: 変更前の確認（ゲームフォルダが在ること・新しいバージョンのツールで適用されていないこと・パッチ専用の場所がリンクでないこと・未対応の記録が無いこと）
/// → plugins.js を戻す → 追加ファイルを削除 → 中断で残った一時ファイルを削除 → 作成したフォルダを削除 → 検証 → バックアップフォルダを削除。
/// 変更前の確認で問題があれば、何も変更せずに中止する。
/// 何度実行しても同じ結果になる（冪等）ように作り、途中で失敗しても再実行で続きを処理できる。
/// バックアップフォルダは最後に削除するため、失敗時も再実行でバックアップを使える。
/// 削除するのはパッチの許可リスト内（<see cref="GamePaths.IsPatchFile"/>・<see cref="GamePaths.IsPatchDirectory"/>）のファイルとフォルダだけ。
/// </summary>
public static class PatchRestorer
{
    /// <summary>フォルダの再帰列挙で、リンク先（ジャンクション・シンボリックリンク）へ入り込まないための設定。</summary>
    private static readonly EnumerationOptions RecursiveWithoutLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// パッチを取り除き、適用前の状態に戻す。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="manifest">埋め込みペイロードの manifest（状態ファイルが無い場合の削除対象の一覧に使う）。</param>
    /// <param name="log">進行状況の通知先。</param>
    public static PatchOperationResult Restore(string gameDirectory, PayloadManifest manifest, Action<string> log) =>
        Restore(gameDirectory, manifest, log, FileOperations.Default);

    /// <summary>
    /// パッチを取り除き、適用前の状態に戻す（ファイル操作を差し替え可能な版。テストで失敗・中断を注入するために使う）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="manifest">埋め込みペイロードの manifest。</param>
    /// <param name="log">進行状況の通知先。</param>
    /// <param name="fileSystem">ファイル操作。</param>
    internal static PatchOperationResult Restore(string gameDirectory, PayloadManifest manifest, Action<string> log, IPatchFileSystem fileSystem)
    {
        try
        {
            RestoreCore(gameDirectory, manifest, log, fileSystem);
            log("元の状態に戻しました。");
            return PatchOperationResult.Succeeded("元の状態（英語）に戻しました。");
        }
        catch (Exception ex)
        {
            // よくある例外は日本語の説明にし、英語の原文は括弧書きで残す（ログ欄とダイアログに出るため。AA-13）
            var described = ErrorDescriber.Describe(ex);
            log($"エラーが発生しました: {described}");
            var message = ex is PatchException
                ? ex.Message
                : $"元に戻す途中でエラーが発生しました。もう一度実行するか、Steam の「ゲームファイルの整合性を確認」を実行してください。{Environment.NewLine}原因: {described}";
            return PatchOperationResult.Failed(message, ex, []);
        }
    }

    /// <summary>復元の本体。</summary>
    private static void RestoreCore(string gameDirectory, PayloadManifest manifest, Action<string> log, IPatchFileSystem fileSystem)
    {
        var snapshot = GameSnapshotReader.Read(gameDirectory, manifest);
        var state = snapshot.State;

        // ゲームフォルダが無くなっていれば（アンインストール・移動）、何も変更せずに起動し直すよう案内する（AA-08）
        if (snapshot.MissingGameReason is { } missingGame)
        {
            throw new PatchException($"{missingGame}{Environment.NewLine}{PatchMessages.GameNotFoundGuidance}");
        }

        // 新しいバージョンのツールで適用された状態ファイル（形式が新しい・許可リスト外の記録）は、このバージョンでは戻し方が分からない。
        // 「状態ファイル無し」として片付けると、扱えない変更を残したまま記録を消してしまうため、何も変更せずに中止する（CA-04）
        if (snapshot.UnsupportedState is { } newerState)
        {
            throw new PatchException(PatchMessages.DescribeRestoreBlockedByNewerVersion(newerState));
        }

        // パッチ専用の場所（www/sentience_jp・バックアップフォルダ・プラグインファイル）がリンクになっていれば、
        // リンク先（ゲームフォルダの外）のファイルや空フォルダを削除しないよう、最初の変更の前に中止する（CA-01）
        PatchLinkGuard.EnsureNoLinks(gameDirectory);

        if (snapshot.StateError is not null)
        {
            log($"状態ファイルを使わずに復元します（{snapshot.StateError}）。");
        }

        // 0. このバージョンが戻し方を知らない変更の記録（将来の版が plugins.js 以外も変更した等）があれば、
        //    バックアップを失わないよう、何も変更せずに中止する
        var unsupported = (state?.ModifiedFiles ?? [])
            .Where(record => !IsPluginsJs(record.Path))
            .Select(record => record.Path)
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new PatchException(
                $"このバージョンのツールでは元に戻せない変更が記録されています（{string.Join(", ", unsupported)}）。"
                + "ゲームのファイルとバックアップは変更していません。適用に使った新しいバージョンのツールで「元に戻す」を実行してください。");
        }

        // 1. plugins.js を戻す
        var record = state?.ModifiedFiles.FirstOrDefault(f => IsPluginsJs(f.Path));
        var restoredFromBackup = RestorePluginsJs(gameDirectory, record, log, fileSystem);

        // 2. 追加ファイルを削除（状態ファイルの記録と manifest の一覧の和集合。どちらも許可リスト内のパスに限られる）
        var filesToDelete = (state?.AddedFiles.Select(f => f.Path) ?? [])
            .Concat(manifest.Files.Select(f => f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in filesToDelete)
        {
            EnsureAllowed(GamePaths.IsPatchFile(path), path);
            var fullPath = GamePaths.ToFullPath(gameDirectory, path);
            if (File.Exists(fullPath))
            {
                log($"ファイルを削除しています: {path}");
                fileSystem.DeleteFile(fullPath);
            }
        }

        // 3. 置き換えの途中で中断して残った一時ファイルを削除する
        var temporaryFiles = GameSnapshotReader.TemporaryFilePaths();
        foreach (var path in temporaryFiles)
        {
            var fullPath = GamePaths.ToFullPath(gameDirectory, path);
            if (File.Exists(fullPath))
            {
                log($"中断で残った一時ファイルを削除しています: {path}");
                fileSystem.DeleteFile(fullPath);
            }
        }

        // 4. パッチが作成したフォルダを、深い順に「空なら」削除する。
        //    状態ファイルがある場合は、適用時に新しく作ったと記録したフォルダだけを対象にする（適用前からあった空の www/sentience_jp などを消さない。CA-15）。
        //    状態ファイルが無い片付けでは、何を作ったか分からないため、従来どおりパッチ専用フォルダとその配下の空フォルダを対象にする。
        var directoriesToDelete = (state is not null
                ? state.CreatedDirectories
                : GameConstants.PatchOwnedDirectories.Concat(ExistingSubdirectories(gameDirectory, GameConstants.PatchOwnedDirectories)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GamePaths.Depth)
            .ToList();
        foreach (var directory in directoriesToDelete)
        {
            EnsureAllowed(GamePaths.IsPatchDirectory(directory), directory);
            if (fileSystem.DeleteDirectoryIfEmpty(GamePaths.ToFullPath(gameDirectory, directory)))
            {
                log($"フォルダを削除しました: {directory}");
            }
        }

        // 5. 検証
        Verify(gameDirectory, record, restoredFromBackup, filesToDelete.Concat(temporaryFiles).ToList(), fileSystem);

        // 6. バックアップフォルダを削除（最後に行うことで、途中失敗時の再実行でもバックアップを使える）
        DeleteBackupDirectory(gameDirectory, state, log, fileSystem);
    }

    /// <summary>
    /// plugins.js を元に戻す。戻し方は適用失敗時の取り消しと同じ規則（<see cref="PluginsJsRevertPlanner"/>）で決める:
    /// - plugins.js が無ければ、記録と一致するバックアップから作り直す。無ければ案内つきで中止する（CA-05）。
    /// - 現在の内容が「パッチ適用直後」と一致し、正しいバックアップがあれば、バックアップで丸ごと戻す。
    /// - 解析できず破損らしい（長さ 0・NUL・UTF-8 不正）場合は、記録と一致するバックアップがあればそれで戻し、無ければ案内つきで中止する。
    ///   破損ではない正しいテキストは上書きせず、プラグイン名を含まなければそのまま残し、含めば案内つきで中止する（CA-07）。
    /// - それ以外でエントリが残っていれば、エントリだけをテキスト上で取り除く（Steam 更新後の新しい内容を保つ）。
    /// - エントリが無ければ何もしない。
    /// ロック・アクセス拒否で読めないだけの場合は「無い」とみなさず、例外のまま失敗させる（読めないファイルを上書きしない）。
    /// </summary>
    /// <returns>バックアップから戻した場合は true。</returns>
    private static bool RestorePluginsJs(string gameDirectory, ModifiedFileRecord? record, Action<string> log, IPatchFileSystem fileSystem)
    {
        var pluginsFullPath = GamePaths.ToFullPath(gameDirectory, GameConstants.PluginsJsRelativePath);
        var currentBytes = PluginsJsReverter.ReadIfExists(fileSystem, pluginsFullPath);

        byte[]? backupBytes = null;
        if (record is not null)
        {
            var backupFullPath = GamePaths.ToFullPath(gameDirectory, record.BackupPath);
            backupBytes = File.Exists(backupFullPath) ? fileSystem.ReadAllBytes(backupFullPath) : null;
        }

        var decision = PluginsJsRevertPlanner.Decide(
            currentBytes is null ? null : PluginsJsObservation.Observe(currentBytes),
            record?.OriginalSha256,
            record?.PatchedSha256,
            backupBytes is null ? null : Hashing.Sha256Hex(backupBytes));

        // plugins.js を作り直すには置き場所（www/js）が必要。フォルダごと無いゲームは、このツールでは直せないため修復を案内する
        if (currentBytes is null
            && decision.Method == PluginsJsRevertMethod.ReplaceWithOriginal
            && !Directory.Exists(Path.GetDirectoryName(pluginsFullPath)))
        {
            throw new PatchException($"plugins.js の置き場所（www/js フォルダ）がありません。{PatchMessages.SteamVerifyGuidance}");
        }

        PluginsJsReverter.Execute(fileSystem, pluginsFullPath, decision, currentBytes, backupBytes, log);
        return decision.Method == PluginsJsRevertMethod.ReplaceWithOriginal;
    }

    /// <summary>復元結果を検証する。問題があれば <see cref="PatchException"/> を投げる。</summary>
    private static void Verify(
        string gameDirectory,
        ModifiedFileRecord? record,
        bool restoredFromBackup,
        IReadOnlyList<string> deletedFiles,
        IPatchFileSystem fileSystem)
    {
        var pluginsFullPath = GamePaths.ToFullPath(gameDirectory, GameConstants.PluginsJsRelativePath);
        var observation = PluginsJsObservation.Observe(fileSystem.ReadAllBytes(pluginsFullPath));
        if (observation.HasEntry is null)
        {
            // 破損ではなく、パッチの登録も含まない「解析できないだけ」の内容は、他のプログラムが書いたものとして意図してそのまま残した（CA-07）
            if (!observation.LooksCorrupt && !observation.MentionsPluginName)
            {
                VerifyDeletedFiles(gameDirectory, deletedFiles);
                return;
            }

            throw new PatchException($"元に戻した後の plugins.js を解析できません（{observation.ParseError}）。{PluginsJsFormat.Guidance}");
        }

        if (observation.HasEntry.Value)
        {
            throw new PatchException("plugins.js からプラグインの登録を取り除けませんでした。");
        }

        if (restoredFromBackup && !string.Equals(observation.Sha256, record?.OriginalSha256, StringComparison.Ordinal))
        {
            throw new PatchException("plugins.js が適用前の内容と一致しません。");
        }

        VerifyDeletedFiles(gameDirectory, deletedFiles);
    }

    /// <summary>削除したはずのファイルが残っていないことを確かめる。残っていれば <see cref="PatchException"/> を投げる。</summary>
    private static void VerifyDeletedFiles(string gameDirectory, IReadOnlyList<string> deletedFiles)
    {
        var remaining = deletedFiles.Where(path => File.Exists(GamePaths.ToFullPath(gameDirectory, path))).ToList();
        if (remaining.Count > 0)
        {
            throw new PatchException($"削除できなかったファイルがあります: {string.Join(", ", remaining)}");
        }
    }

    /// <summary>
    /// バックアップフォルダを削除する。このツールが作るはずのないファイル（未対応の記録のバックアップ・新しい版の状態ファイル等）が
    /// 含まれている場合は、それを失わないよう削除せずに中止し、利用者に確認を求める。
    /// </summary>
    private static void DeleteBackupDirectory(string gameDirectory, PatchStateFile? state, Action<string> log, IPatchFileSystem fileSystem)
    {
        var backupFullPath = GamePaths.ToFullPath(gameDirectory, BackupLayout.BackupDirectory);
        if (!Directory.Exists(backupFullPath))
        {
            return;
        }

        var unexpected = FindUnexpectedBackupFiles(gameDirectory, state);
        if (unexpected.Count > 0)
        {
            throw new PatchException(
                $"バックアップフォルダ（{GameConstants.BackupDirectoryName}）に、このツールが作成していないファイルがあるため、フォルダを削除せずに残しました: "
                + $"{string.Join(", ", unexpected)}{Environment.NewLine}"
                + "ゲームは元の状態に戻っています。新しいバージョンのツールで適用した場合は、そのツールで「元に戻す」を実行してください。"
                + "心当たりが無ければ、内容を確認のうえ手動で削除してください。");
        }

        log("バックアップフォルダを削除しています…");
        fileSystem.DeleteDirectoryRecursive(backupFullPath);
    }

    /// <summary>
    /// バックアップフォルダ内の、想定外のファイル（ゲームルートからの相対パス）を返す。
    /// 想定内: 状態ファイル、plugins.js のコピー、状態に記録されたバックアップ、およびそれらの一時ファイル。
    /// </summary>
    private static List<string> FindUnexpectedBackupFiles(string gameDirectory, PatchStateFile? state)
    {
        var known = new[] { BackupLayout.StateFile, BackupLayout.BackupPathFor(GameConstants.PluginsJsRelativePath) }
            .Concat(state?.ModifiedFiles.Select(f => f.BackupPath) ?? [])
            .SelectMany(path => new[] { path, path + GameConstants.TemporaryFileSuffix })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var gameFullPath = Path.GetFullPath(gameDirectory);
        var backupFullPath = GamePaths.ToFullPath(gameDirectory, BackupLayout.BackupDirectory);
        return Directory.EnumerateFiles(backupFullPath, "*", RecursiveWithoutLinks)
            .Select(full => Path.GetRelativePath(gameFullPath, full).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !known.Contains(relative))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// パッチ専用フォルダ配下に現存するサブフォルダを、相対パスで列挙する（リンク先には入らない）。
    /// 利用者が作った、パスとして扱えない名前（末尾の "." など）のフォルダは対象外にして触らない（その親フォルダも空にならないため残る）。
    /// ルート自体がリパースポイント（ジャンクション・シンボリックリンク）の場合は、リンク先を列挙してしまうため何も返さない
    /// （CA-01 の多層防御。通常は復元の最初にリンクを検出して中止するため、ここには到達しない）。
    /// </summary>
    internal static IEnumerable<string> ExistingSubdirectories(string gameDirectory, IEnumerable<string> roots) =>
        roots.SelectMany(root =>
        {
            var rootFullPath = GamePaths.ToFullPath(gameDirectory, root);
            if (!Directory.Exists(rootFullPath) || File.GetAttributes(rootFullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return Enumerable.Empty<string>();
            }

            var gameFullPath = Path.GetFullPath(gameDirectory);
            return Directory.EnumerateDirectories(rootFullPath, "*", RecursiveWithoutLinks)
                .Select(full => Path.GetRelativePath(gameFullPath, full).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(GamePaths.IsPatchDirectory);
        });

    /// <summary>相対パスが plugins.js を指すか。</summary>
    private static bool IsPluginsJs(string relativePath) =>
        string.Equals(relativePath, GameConstants.PluginsJsRelativePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 削除しようとしているパスが許可リスト内であることを確認する（manifest・状態ファイルの読み込み時にも検証済みだが、念のための二重確認）。
    /// </summary>
    private static void EnsureAllowed(bool allowed, string relativePath)
    {
        if (!allowed)
        {
            throw new PatchException($"パッチの管理外のパスを削除しようとしたため中止しました: \"{relativePath}\"");
        }
    }
}
