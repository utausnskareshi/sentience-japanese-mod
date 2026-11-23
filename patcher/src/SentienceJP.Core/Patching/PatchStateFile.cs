using System.Text.Json;

namespace SentienceJP.Core.Patching;

/// <summary>適用時に追加したファイルの記録。</summary>
/// <param name="Path">ゲームルートからの相対パス（"/" 区切り）。</param>
/// <param name="Sha256">書き込んだ内容の SHA-256。</param>
public sealed record AddedFileRecord(string Path, string Sha256);

/// <summary>適用時に変更したファイルの記録。</summary>
/// <param name="Path">ゲームルートからの相対パス（"/" 区切り）。</param>
/// <param name="OriginalSha256">変更前の SHA-256。</param>
/// <param name="PatchedSha256">変更後の SHA-256。</param>
/// <param name="BackupPath">元ファイルのコピーの場所（ゲームルートからの相対パス）。</param>
public sealed record ModifiedFileRecord(string Path, string OriginalSha256, string PatchedSha256, string BackupPath);

/// <summary>
/// 適用状態を記録する state.json の内容。復元時に「何を元に戻すか」の根拠になる。
/// </summary>
/// <param name="Schema">形式バージョン。</param>
/// <param name="PatchVersion">適用したパッチのバージョン。</param>
/// <param name="TranslationVersion">適用した訳文データのバージョン。</param>
/// <param name="AppliedAt">適用日時。</param>
/// <param name="GameBuildId">適用時のゲームのビルドID（不明なら null）。</param>
/// <param name="AddedFiles">追加したファイル（パッチの許可リスト内のパスのみ）。</param>
/// <param name="ModifiedFiles">
/// 変更したファイル。形式バージョン 1 では plugins.js の記録だけを扱う。
/// 他のファイルの変更を記録するように意味を広げる場合は、<see cref="GameConstants.StateSchemaVersion"/> を上げ、
/// 復元側の対応を先に用意すること（このバージョンの復元は、未対応の記録があると何も変更せずに中止する）。
/// </param>
/// <param name="CreatedDirectories">新規に作成したフォルダ（ゲームルートからの相対パス、浅い順。パッチの許可リスト内のみ）。</param>
public sealed record PatchStateFile(
    int Schema,
    string PatchVersion,
    string TranslationVersion,
    DateTimeOffset AppliedAt,
    string? GameBuildId,
    IReadOnlyList<AddedFileRecord> AddedFiles,
    IReadOnlyList<ModifiedFileRecord> ModifiedFiles,
    IReadOnlyList<string> CreatedDirectories);

/// <summary>
/// このバージョンのツールでは扱えない（新しいバージョンのツールで適用された）状態ファイルの情報（CA-04）。
/// 「破損」（JSON 不正・型違い・null・不正なハッシュ・構文として不正なパス）とは区別し、
/// 適用も復元も何も変更せずに中止して、適用に使った新しいバージョンのツールで元に戻すよう案内するために使う。
/// </summary>
/// <param name="Reason">扱えない理由（利用者向けの日本語）。</param>
/// <param name="PatchVersion">状態ファイルに記録された patchVersion（形式が正しく読み取れた場合だけ。表示用）。</param>
/// <param name="Entries">このバージョンでは扱えない記録（パス）。状態ファイルの形式（schema）自体が新しい場合は空。</param>
public sealed record UnsupportedPatchState(string Reason, string? PatchVersion, IReadOnlyList<string> Entries)
{
    /// <summary>扱えないものの要約（記録があればそのパスの一覧、無ければ理由）。</summary>
    public string Summary => Entries.Count > 0 ? string.Join(", ", Entries) : Reason;
}

/// <summary>
/// state.json の読み取り結果。正常・破損・未対応のいずれか（<see cref="PatchStateSerializer.Read"/> が返す）。
/// </summary>
/// <param name="State">読み取れた状態（正常なとき。未対応でも内容自体は読めた場合は参考として入る。破損なら null）。</param>
/// <param name="CorruptReason">破損の説明（破損でなければ null）。</param>
/// <param name="CorruptCause">破損の原因となった例外（JSON の構文エラーなど。無ければ null）。</param>
/// <param name="Unsupported">このバージョンでは扱えない場合の情報（未対応でなければ null）。</param>
public sealed record PatchStateReadResult(
    PatchStateFile? State,
    string? CorruptReason,
    Exception? CorruptCause,
    UnsupportedPatchState? Unsupported)
{
    /// <summary>このバージョンでそのまま使える正常な状態か。</summary>
    public bool IsValid => State is not null && CorruptReason is null && Unsupported is null;

    /// <summary>正常な結果を作る。</summary>
    /// <param name="state">読み取れた状態。</param>
    public static PatchStateReadResult Valid(PatchStateFile state) => new(state, null, null, null);

    /// <summary>破損の結果を作る。</summary>
    /// <param name="reason">破損の説明。</param>
    /// <param name="cause">原因の例外（あれば）。</param>
    public static PatchStateReadResult Corrupt(string reason, Exception? cause = null) => new(null, reason, cause, null);

    /// <summary>未対応の結果を作る。</summary>
    /// <param name="unsupported">未対応の情報。</param>
    /// <param name="state">内容自体は読めた場合の状態（参考。読めなければ null）。</param>
    public static PatchStateReadResult NotSupported(UnsupportedPatchState unsupported, PatchStateFile? state = null) =>
        new(state, null, null, unsupported);
}

/// <summary>state.json の読み書き。</summary>
public static class PatchStateSerializer
{
    /// <summary>
    /// 読み書きの設定。null や欠落を許さない厳密な読み込みにして、壊れた状態ファイルを確実に検出する。
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>状態をバイト列（UTF-8 の JSON）に変換する。</summary>
    /// <param name="state">保存する状態。</param>
    public static byte[] Serialize(PatchStateFile state) => JsonSerializer.SerializeToUtf8Bytes(state, Options);

    /// <summary>
    /// バイト列から状態を読み込み、内容を検証する（従来どおり、問題があれば例外にする版。テストや状態ファイルの編集に使う）。
    /// 破損・未対応のどちらも <see cref="PatchException"/> になる。ただし、plugins.js 以外の変更の記録（将来の版が残しうるもの）だけは
    /// 読み込みを許す（C-11。復元は <see cref="Read"/> の分類に従って、何も変更せずに中止する）。
    /// 状態判定・適用・復元は、破損と未対応を区別できる <see cref="Read"/> を使う。
    /// </summary>
    /// <param name="json">state.json の内容。</param>
    /// <exception cref="PatchException">JSON が不正、または内容が契約に反する場合。</exception>
    public static PatchStateFile Deserialize(byte[] json)
    {
        var result = Read(json);
        if (result.CorruptReason is { } reason)
        {
            throw result.CorruptCause is { } cause ? new PatchException(reason, cause) : new PatchException(reason);
        }

        if (result.State is not { } state)
        {
            // 形式（schema）が新しく、内容を読めなかった場合
            throw new PatchException(result.Unsupported?.Reason ?? $"状態ファイル（{GameConstants.StateFileName}）を読み込めません。");
        }

        // 許可リスト外の追加ファイル・作成フォルダや、想定外のバックアップの場所は、従来どおり内容の不正として扱う
        var strictProblem = DescribeStrictlyUnsupported(state).FirstOrDefault();
        if (strictProblem is not null)
        {
            throw new PatchException($"状態ファイル（{GameConstants.StateFileName}）の内容が不正です: {strictProblem}");
        }

        return state;
    }

    /// <summary>
    /// 状態ファイルを読み取り、「正常」「破損」「未対応」に分類する（CA-04）。例外は投げない。
    /// - 破損: JSON として不正・長さ 0・型違い・null・不正なハッシュ・構文として不正なパス（".."・絶対パス・Windows の別名など）。
    ///   呼び出し側は従来どおり「状態ファイル無し」として片付ける。
    /// - 未対応: 形式（schema）が現行より新しい、または構文は正しいが現行の許可リスト外のファイル・フォルダ・変更・バックアップの場所の記録。
    ///   新しいバージョンのツールで適用されたものとして、適用も復元も何も変更せずに中止する。
    /// 長さ 0（電源断）や BOM 付き（JSON として読めない）のファイルは破損として扱い、従来どおり片付けで回復できる。
    /// </summary>
    /// <param name="json">state.json の内容。</param>
    public static PatchStateReadResult Read(byte[] json)
    {
        // 1. 形式（schema）が新しければ、内容の形が違いうるため、全体を読み込む前に「未対応」とする
        if (TryReadNewerSchema(json) is { } newer)
        {
            return PatchStateReadResult.NotSupported(newer);
        }

        // 2. 全体を読み込む。JSON の構文・型・必須項目の誤りは、すべて「壊れている」として扱う
        PatchStateFile? state;
        try
        {
            state = JsonSerializer.Deserialize<PatchStateFile>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return PatchStateReadResult.Corrupt($"状態ファイル（{GameConstants.StateFileName}）が壊れています: {ex.Message}", ex);
        }

        if (state is null)
        {
            return PatchStateReadResult.Corrupt($"状態ファイル（{GameConstants.StateFileName}）が空です。");
        }

        if (state.Schema != GameConstants.StateSchemaVersion)
        {
            // 新しい形式は 1. で除いたため、ここに来るのは現行より古い（存在しない）形式＝破損
            return PatchStateReadResult.Corrupt($"未対応の状態ファイル形式です（schema={state.Schema}）。");
        }

        // 3. 中身の破損（null・不正なハッシュ・構文として不正なパス）
        if (DescribeCorruption(state) is { } corruption)
        {
            return PatchStateReadResult.Corrupt($"状態ファイル（{GameConstants.StateFileName}）の内容が不正です: {corruption}");
        }

        // 4. 構文は正しいが、このバージョンでは扱えない記録（新しいバージョンのツールが残したもの）
        var unsupportedEntries = UnsupportedEntries(state);
        if (unsupportedEntries.Count > 0)
        {
            var version = PatchVersionFormat.IsValid(state.PatchVersion) ? state.PatchVersion : null;
            return PatchStateReadResult.NotSupported(
                new UnsupportedPatchState(
                    $"状態ファイルに、このバージョンのツールでは扱えない記録があります: {string.Join(", ", unsupportedEntries)}",
                    version,
                    unsupportedEntries),
                state);
        }

        return PatchStateReadResult.Valid(state);
    }

    /// <summary>
    /// JSON の最上位の "schema" が整数で、現行の形式より新しければ、未対応の情報を返す（それ以外は null）。
    /// JSON として読めない場合は null を返し、続く全体の読み込みで「破損」として報告させる。
    /// </summary>
    private static UnsupportedPatchState? TryReadNewerSchema(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schema)
                || schema <= GameConstants.StateSchemaVersion)
            {
                return null;
            }

            // 表示用に patchVersion も読む（形式が正しい場合だけ。読めなくても未対応の判定には影響しない）
            var version = root.TryGetProperty("patchVersion", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;
            return new UnsupportedPatchState(
                $"状態ファイルの形式（schema={schema}）が、このツールの対応する形式（schema={GameConstants.StateSchemaVersion}）より新しいです。",
                PatchVersionFormat.IsValid(version) ? version : null,
                []);
        }
        catch (JsonException)
        {
            // JSON として読めないものは「未対応」ではない。全体の読み込み（Read の 2.）で、原因つきの「破損」として報告する
            return null;
        }
    }

    /// <summary>
    /// 読み込んだ状態の中身の破損を調べる純粋関数。問題が無ければ null、あればその説明を返す。
    /// - 配列の要素や項目に null が無いこと（JSON の null が紛れ込んだ破損への備え。配列要素の null は自動検査の対象外のため）
    /// - ハッシュが SHA-256 の 16 進小文字であること
    /// - パス（追加ファイル・変更ファイル・バックアップの場所・作成フォルダ）が構文として正しいこと（<see cref="GamePaths.DescribeProblem"/>）
    /// </summary>
    private static string? DescribeCorruption(PatchStateFile state)
    {
        foreach (var file in state.AddedFiles)
        {
            if (file is null || file.Path is null || file.Sha256 is null)
            {
                return "addedFiles に null の要素または項目があります。";
            }

            if (GamePaths.DescribeProblem(file.Path) is { } pathProblem)
            {
                return $"addedFiles に不正なパスがあります（{pathProblem}）: \"{file.Path}\"";
            }

            if (!Hashing.IsSha256Hex(file.Sha256))
            {
                return $"addedFiles の sha256 が不正です（{file.Path}）。";
            }
        }

        foreach (var file in state.ModifiedFiles)
        {
            if (file is null || file.Path is null || file.OriginalSha256 is null || file.PatchedSha256 is null || file.BackupPath is null)
            {
                return "modifiedFiles に null の要素または項目があります。";
            }

            if (GamePaths.DescribeProblem(file.Path) is { } pathProblem)
            {
                return $"modifiedFiles に不正なパスがあります（{pathProblem}）: \"{file.Path}\"";
            }

            if (GamePaths.DescribeProblem(file.BackupPath) is { } backupProblem)
            {
                return $"modifiedFiles のバックアップの場所が不正です（{backupProblem}）: \"{file.BackupPath}\"";
            }

            if (!Hashing.IsSha256Hex(file.OriginalSha256) || !Hashing.IsSha256Hex(file.PatchedSha256))
            {
                return $"modifiedFiles の sha256 が不正です（{file.Path}）。";
            }
        }

        foreach (var directory in state.CreatedDirectories)
        {
            if (directory is null)
            {
                return "createdDirectories に null の要素があります。";
            }

            if (GamePaths.DescribeProblem(directory) is { } directoryProblem)
            {
                return $"createdDirectories に不正なパスがあります（{directoryProblem}）: \"{directory}\"";
            }
        }

        return null;
    }

    /// <summary>
    /// 構文は正しいが、このバージョンでは扱えない記録（パス）を列挙する純粋関数（破損が無いことを確かめた後に呼ぶ）。
    /// 許可リスト外の追加ファイル・作成フォルダ・想定外のバックアップの場所（<see cref="DescribeStrictlyUnsupported"/>）と、
    /// plugins.js 以外の変更の記録（形式 1 では扱わない。C-11）。
    /// </summary>
    private static List<string> UnsupportedEntries(PatchStateFile state) =>
        state.AddedFiles.Where(file => !GamePaths.IsPatchFile(file.Path)).Select(file => file.Path)
            .Concat(state.ModifiedFiles.Where(file => !IsPluginsJs(file.Path)).Select(file => file.Path))
            .Concat(state.ModifiedFiles.Where(file => !IsExpectedBackupPath(file)).Select(file => file.BackupPath))
            .Concat(state.CreatedDirectories.Where(directory => !GamePaths.IsPatchDirectory(directory)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 従来の読み込み（<see cref="Deserialize"/>）でも内容の不正として扱う、許可リスト外の記録の説明を列挙する純粋関数
    /// （追加ファイル・作成フォルダが許可リスト外、またはバックアップの場所が決まった配置（<see cref="BackupLayout.BackupPathFor"/>）と異なる）。
    /// </summary>
    private static IEnumerable<string> DescribeStrictlyUnsupported(PatchStateFile state) =>
        state.AddedFiles.Where(file => !GamePaths.IsPatchFile(file.Path))
            .Select(file => $"addedFiles にパッチのファイルではないパスがあります: \"{file.Path}\"")
            .Concat(state.ModifiedFiles.Where(file => !IsExpectedBackupPath(file))
                .Select(file => $"modifiedFiles のバックアップの場所が想定と異なります: \"{file.BackupPath}\""))
            .Concat(state.CreatedDirectories.Where(directory => !GamePaths.IsPatchDirectory(directory))
                .Select(directory => $"createdDirectories にパッチのフォルダではないパスがあります: \"{directory}\""));

    /// <summary>変更ファイルのバックアップの場所が、決まった配置と一致するか（大文字小文字は区別しない）。</summary>
    private static bool IsExpectedBackupPath(ModifiedFileRecord file) =>
        string.Equals(file.BackupPath, BackupLayout.BackupPathFor(file.Path), StringComparison.OrdinalIgnoreCase);

    /// <summary>相対パスが plugins.js を指すか（大文字小文字は区別しない）。</summary>
    private static bool IsPluginsJs(string relativePath) =>
        string.Equals(relativePath, GameConstants.PluginsJsRelativePath, StringComparison.OrdinalIgnoreCase);
}
