using SentienceJP.Core.Payload;

namespace SentienceJP.Core.Patching;

/// <summary>plugins.js を使えない理由の種類（CA-05・CA-08。理由ごとに案内の文言と、取れる操作が異なる）。</summary>
public enum PluginsJsProblemKind
{
    /// <summary>ファイルが無い（ウイルス対策ソフトの隔離・誤削除など）。記録と一致するバックアップがあれば作り直せる。</summary>
    Missing,

    /// <summary>ファイルはあるが読めない（他のプログラムが使用中・アクセス拒否）。原因を取り除けば読める。無いものとして扱ってはいけない。</summary>
    Unreadable,

    /// <summary>読めたが、このツールが想定する形式として解析できない（破損・他の MOD による編集など）。</summary>
    Unparsable,
}

/// <summary>
/// 状態判定に必要な、ゲームフォルダの現在の様子を一度に読み取ったもの。
/// 読み取り（副作用）と判定（純粋関数）を分離するための中間データ。
/// </summary>
/// <param name="GameDirectory">ゲームのルートフォルダ。</param>
/// <param name="State">state.json の内容（無い、または壊れている場合は null）。</param>
/// <param name="StateError">state.json が存在するが読めなかった場合のエラー内容。</param>
/// <param name="PluginsJsSha256">plugins.js の SHA-256（読めなかった場合は null）。</param>
/// <param name="PluginsJsHasEntry">plugins.js にパッチのエントリがあるか。</param>
/// <param name="PluginsJsError">plugins.js を読めなかった・解析できなかった場合のエラー内容。</param>
/// <param name="PayloadFileHashes">パッチが扱うファイル（manifest と state の和集合）の相対パス → 現在の SHA-256（無ければ null）。</param>
/// <param name="BackupDirectoryExists">バックアップフォルダが存在するか。</param>
/// <param name="LeftoverTemporaryFiles">中断で残った一時ファイル（ゲームルートからの相対パス）。</param>
/// <param name="UnsupportedState">
/// 状態ファイルが、このバージョンのツールでは扱えない（新しいバージョンのツールで適用された）場合の情報（CA-04。それ以外は null）。
/// このとき <see cref="State"/> と <see cref="StateError"/> は null（扱えない状態ファイルの記録は使わず、「破損」としても扱わない）。
/// </param>
/// <param name="PluginsJsProblem">
/// plugins.js を使えない理由の種類（<see cref="PluginsJsError"/> がある場合。CA-05）。
/// 指定が無い場合は、従来どおり「解析できない」として扱う（<see cref="EffectivePluginsJsProblem"/>）。
/// </param>
/// <param name="MissingGameReason">
/// ゲームフォルダが無くなっている（移動・アンインストール）場合の説明（AA-08。それ以外は null）。
/// このときは他の項目を読み取らない（すべて空）。
/// </param>
public sealed record GameSnapshot(
    string GameDirectory,
    PatchStateFile? State,
    string? StateError,
    string? PluginsJsSha256,
    bool PluginsJsHasEntry,
    string? PluginsJsError,
    IReadOnlyDictionary<string, string?> PayloadFileHashes,
    bool BackupDirectoryExists,
    IReadOnlyList<string> LeftoverTemporaryFiles,
    UnsupportedPatchState? UnsupportedState = null,
    PluginsJsProblemKind? PluginsJsProblem = null,
    string? MissingGameReason = null)
{
    /// <summary>
    /// ゲームフォルダが無くなっている場合のスナップショットを作る（他の項目は読み取らず空にする）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="reason">無くなっている理由の説明。</param>
    public static GameSnapshot ForMissingGame(string gameDirectory, string reason) =>
        new(gameDirectory, null, null, null, false, null, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), false, [], MissingGameReason: reason);

    /// <summary>
    /// plugins.js を使えない理由の種類（問題が無ければ null）。
    /// <see cref="PluginsJsError"/> があるのに種類の指定が無い場合は、従来の意味（解析できない）とみなす。
    /// </summary>
    public PluginsJsProblemKind? EffectivePluginsJsProblem =>
        PluginsJsError is null ? null : PluginsJsProblem ?? PluginsJsProblemKind.Unparsable;
}

/// <summary>
/// ゲームフォルダからスナップショットを読み取る（ファイル読み取りのみで、書き込みは行わない）。
/// 内容の読み取り（state.json・plugins.js・追加ファイルのハッシュ）は、ウイルス対策ソフト等が一瞬ファイルを開いていることによる
/// 共有違反を短く再試行する（CA-11。適用・復元のファイル操作と同じ方針）。
/// </summary>
public static class GameSnapshotReader
{
    /// <summary>
    /// ゲームフォルダの現在の状態を読み取る（共有違反は既定の待ち時間で再試行する）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="manifest">埋め込みペイロードの manifest（追加ファイルの一覧に使う）。</param>
    public static GameSnapshot Read(string gameDirectory, PayloadManifest manifest) =>
        Read(gameDirectory, manifest, SharingViolationRetry.Default);

    /// <summary>
    /// ゲームフォルダの現在の状態を読み取る（再試行の方針を指定する版。テストで待ち時間を変えるために使う）。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="manifest">埋め込みペイロードの manifest（追加ファイルの一覧に使う）。</param>
    /// <param name="retry">内容の読み取りで使う、共有違反の再試行の方針。</param>
    internal static GameSnapshot Read(string gameDirectory, PayloadManifest manifest, SharingViolationRetry retry)
    {
        // 起動時に検出したゲームフォルダが無くなっていれば（アンインストール・移動・外付けドライブの取り外し）、何も読まずにそのことだけを返す（AA-08）
        if (GameValidator.DescribeMissingGame(gameDirectory) is { } missingGame)
        {
            return GameSnapshot.ForMissingGame(gameDirectory, missingGame);
        }

        var (state, stateError, unsupportedState) = ReadState(gameDirectory, retry);
        var (pluginsSha, hasEntry, pluginsError, pluginsProblem) = ReadPluginsJs(gameDirectory, retry);

        var trackedPaths = manifest.Files.Select(f => f.Path)
            .Concat(state?.AddedFiles.Select(f => f.Path) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var hashes = trackedPaths.ToDictionary(
            path => path,
            path => HashIfExists(GamePaths.ToFullPath(gameDirectory, path), retry),
            StringComparer.OrdinalIgnoreCase);

        var backupExists = Directory.Exists(GamePaths.ToFullPath(gameDirectory, BackupLayout.BackupDirectory));
        var leftoverTemporaryFiles = TemporaryFilePaths()
            .Where(path => File.Exists(GamePaths.ToFullPath(gameDirectory, path)))
            .ToList();
        return new GameSnapshot(
            gameDirectory,
            state,
            stateError,
            pluginsSha,
            hasEntry,
            pluginsError,
            hashes,
            backupExists,
            leftoverTemporaryFiles,
            unsupportedState,
            pluginsProblem);
    }

    /// <summary>
    /// 置き換えの途中で中断した場合に残りうる一時ファイルの相対パス（"/" 区切り）を返す。
    /// 名前の規則は <see cref="GameConstants.TemporaryFileSuffix"/> で一元管理する。
    /// </summary>
    public static IReadOnlyList<string> TemporaryFilePaths() =>
        GameConstants.FilesReplacedViaTemporary
            .Select(path => path + GameConstants.TemporaryFileSuffix)
            .ToList();

    /// <summary>
    /// state.json を読む。無ければ (null, null, null)、壊れている・読めない場合は (null, エラー内容, null)、
    /// このバージョンでは扱えない（新しいバージョンのツールで適用された）場合は (null, null, 未対応の情報)（CA-04）。
    /// 状態ファイルに由来する失敗（破損・未対応・読み取りエラー）は値として返し、例外として呼び出し元（状態判定・画面）へ漏らさない。
    /// </summary>
    private static (PatchStateFile? State, string? Error, UnsupportedPatchState? Unsupported) ReadState(string gameDirectory, SharingViolationRetry retry)
    {
        var path = GamePaths.ToFullPath(gameDirectory, BackupLayout.StateFile);
        if (!File.Exists(path))
        {
            return (null, null, null);
        }

        byte[] bytes;
        try
        {
            bytes = retry.Run(() => File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"状態ファイルを読み取れません: {ErrorDescriber.Describe(ex)}", null);
        }

        var result = PatchStateSerializer.Read(bytes);
        return result.IsValid
            ? (result.State, null, null)
            : (null, result.CorruptReason, result.Unsupported);
    }

    /// <summary>
    /// plugins.js を読み、ハッシュとエントリの有無を返す。使えない場合は、その説明と理由の種類（無い・読めない・解析できない）を返す（CA-05）。
    /// 「無い」はファイル（またはフォルダ）が見つからない場合だけにし、ロックや権限で読めないだけの場合を「無い」として扱わない
    /// （無い場合はバックアップから作り直すため、読めないだけのファイルを上書きしないようにする）。
    /// </summary>
    private static (string? Sha256, bool HasEntry, string? Error, PluginsJsProblemKind? Problem) ReadPluginsJs(string gameDirectory, SharingViolationRetry retry)
    {
        var path = GamePaths.ToFullPath(gameDirectory, GameConstants.PluginsJsRelativePath);
        byte[] bytes;
        try
        {
            bytes = retry.Run(() => File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, false, $"plugins.js がありません（{GameConstants.PluginsJsRelativePath}）。", PluginsJsProblemKind.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, false, $"plugins.js を読み取れません（他のプログラムが使用中か、アクセスが拒否されています）: {ErrorDescriber.Describe(ex)}", PluginsJsProblemKind.Unreadable);
        }

        var observation = PluginsJsObservation.Observe(bytes);
        return (
            observation.Sha256,
            observation.HasEntry ?? false,
            observation.ParseError,
            observation.ParseError is null ? null : PluginsJsProblemKind.Unparsable);
    }

    /// <summary>
    /// ファイルが存在すればその SHA-256 を、無ければ null を返す（共有違反は再試行する）。
    /// 存在の確認と読み取りの間に消えた場合も、無いものとして扱う。
    /// </summary>
    private static string? HashIfExists(string fullPath, SharingViolationRetry retry)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            return retry.Run(() => Hashing.Sha256HexOfFile(fullPath));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
