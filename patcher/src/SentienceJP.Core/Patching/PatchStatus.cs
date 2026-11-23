using System.Globalization;
using SentienceJP.Core.Payload;

namespace SentienceJP.Core.Patching;

/// <summary>パッチの適用状態の種類。</summary>
public enum PatchStatusKind
{
    /// <summary>未適用（英語のまま）。</summary>
    NotApplied,

    /// <summary>適用済み（日本語化されている）。</summary>
    Applied,

    /// <summary>
    /// 要再適用。状態ファイルはあるが、plugins.js のエントリや追加ファイルが失われている
    /// （Steam の更新や「ゲームファイルの整合性を確認」で元に戻された可能性が高い）。
    /// </summary>
    NeedsReapply,

    /// <summary>
    /// 不整合。「元に戻す（クリーンアップ）」で片付けるべき状態。次のいずれか:
    /// - 状態ファイルが無い（または壊れている）のに、パッチの痕跡（エントリ・ファイル・バックアップ・一時ファイル）が残っている
    /// - 状態ファイルはあるが、plugins.js を使えない（無い・読めない・解析できない。無い・破損ならバックアップから戻せる。CA-05・CA-07）
    /// - 状態ファイルはあるが、エントリも追加ファイルも無い（元に戻す処理が最後のバックアップ削除で止まった等）
    /// </summary>
    Inconsistent,

    /// <summary>旧バージョンのパッチが適用されている。</summary>
    Outdated,

    /// <summary>
    /// 新しいバージョンのツールで適用されている（CA-04）。状態ファイルの形式が現行より新しい、または現行の許可リスト外の記録がある。
    /// このバージョンでは戻し方が分からないため、適用も復元も行わず、主ボタンは「再確認する」にする。
    /// </summary>
    NewerVersionApplied,

    /// <summary>
    /// 未適用だが plugins.js を使えない（無い・読めない・解析できない）ため、日本語化できない（CA-08）。
    /// 状態ファイルもパッチの痕跡も無く、根拠が plugins.js の問題だけの場合。「元に戻す」では直らないため、
    /// 主ボタンは「再確認する」にし、理由に応じた対処（Steam の整合性確認など）を案内する。
    /// </summary>
    PluginsJsUnusable,

    /// <summary>
    /// 起動時に検出したゲームフォルダが無くなった（移動・アンインストール・外付けドライブの取り外し）（AA-08）。
    /// 操作は行わず、主ボタンは「再確認する」にして、ツールを起動し直すよう案内する。
    /// </summary>
    GameNotFound,
}

/// <summary>状態判定の結果。</summary>
/// <param name="Kind">状態の種類。</param>
/// <param name="AppliedPatchVersion">適用済みのパッチのバージョン（不明・未適用なら null）。</param>
/// <param name="Reasons">判定の根拠（ログや詳細表示用の日本語メッセージ）。</param>
public sealed record PatchStatus(PatchStatusKind Kind, string? AppliedPatchVersion, IReadOnlyList<string> Reasons);

/// <summary>
/// スナップショットと manifest から適用状態を判定する純粋関数。
/// </summary>
public static class PatchStatusDetector
{
    /// <summary>
    /// 適用状態を判定する。
    /// </summary>
    /// <param name="snapshot">ゲームフォルダの現在の状態。</param>
    /// <param name="manifest">埋め込みペイロードの manifest。</param>
    public static PatchStatus Detect(GameSnapshot snapshot, PayloadManifest manifest)
    {
        // ゲームフォルダが無くなっていれば、片付けなどの操作は出さず、起動し直すよう案内する（AA-08）
        if (snapshot.MissingGameReason is { } missingGame)
        {
            return new PatchStatus(PatchStatusKind.GameNotFound, null, [missingGame, PatchMessages.GameNotFoundGuidance]);
        }

        // 新しいバージョンのツールで適用された状態ファイル（形式が新しい・許可リスト外の記録）は、このバージョンでは扱わない（CA-04）
        if (snapshot.UnsupportedState is { } unsupported)
        {
            return new PatchStatus(
                PatchStatusKind.NewerVersionApplied,
                unsupported.PatchVersion,
                [unsupported.Reason, PatchMessages.NewerVersionGuidance]);
        }

        var state = snapshot.State;
        if (state is null)
        {
            return DetectWithoutState(snapshot);
        }

        // plugins.js を使えない（無い・読めない・解析できない）場合は再適用もできないため、復元を主操作にする。
        // 「元に戻す」で何が起きるか（無ければバックアップから作り直す、など）は理由ごとに案内する（CA-05）
        if (snapshot.PluginsJsError is not null && snapshot.EffectivePluginsJsProblem is { } problem)
        {
            return new PatchStatus(
                PatchStatusKind.Inconsistent,
                state.PatchVersion,
                [snapshot.PluginsJsError, PatchMessages.DescribeRestoreHintForPluginsJs(problem)]);
        }

        // エントリも追加ファイルも無く、状態ファイル（とバックアップ）だけが残っている場合は、
        // 元に戻す処理が途中（最後のバックアップ削除など）で止まった可能性が高い。再適用ではなく片付けを主操作にする。
        if (!snapshot.PluginsJsHasEntry && snapshot.PayloadFileHashes.Values.All(hash => hash is null))
        {
            return new PatchStatus(
                PatchStatusKind.Inconsistent,
                state.PatchVersion,
                ["パッチのエントリとファイルは無く、状態ファイルとバックアップだけが残っています（元に戻す処理が途中で止まった可能性があります）。"]);
        }

        var reasons = new List<string>();
        if (!snapshot.PluginsJsHasEntry)
        {
            reasons.Add("plugins.js にパッチのエントリがありません（Steam の更新や整合性チェックで元に戻された可能性があります）。");
        }

        var missingOrChanged = state.AddedFiles
            .Where(f => !snapshot.PayloadFileHashes.TryGetValue(f.Path, out var actual) || !string.Equals(actual, f.Sha256, StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();
        if (missingOrChanged.Count > 0)
        {
            reasons.Add($"パッチのファイルが欠けているか変更されています: {string.Join(", ", missingOrChanged)}");
        }

        if (reasons.Count > 0)
        {
            return new PatchStatus(PatchStatusKind.NeedsReapply, state.PatchVersion, reasons);
        }

        if (PatchVersionComparer.IsOlder(state.PatchVersion, manifest.PatchVersion))
        {
            return new PatchStatus(
                PatchStatusKind.Outdated,
                state.PatchVersion,
                [$"適用済みのバージョン（{state.PatchVersion}）が、このツールのバージョン（{manifest.PatchVersion}）より古いです。"]);
        }

        return new PatchStatus(PatchStatusKind.Applied, state.PatchVersion, ["パッチは適用済みです。"]);
    }

    /// <summary>
    /// 状態ファイルが無い（または壊れている）場合の判定。
    /// パッチの痕跡があれば「不整合」（元に戻すで片付ける）。痕跡が無く、根拠が plugins.js の問題だけなら「plugins.js を使えない」（CA-08）。
    /// </summary>
    private static PatchStatus DetectWithoutState(GameSnapshot snapshot)
    {
        var traces = FindTracesWithoutState(snapshot);
        if (traces.Count > 0)
        {
            // 痕跡と一緒に plugins.js の問題も根拠として示す（片付けの前に修復が必要なことが分かるように）
            return new PatchStatus(
                PatchStatusKind.Inconsistent,
                null,
                snapshot.PluginsJsError is { } error ? [.. traces, error] : traces);
        }

        if (snapshot.PluginsJsError is { } pluginsError && snapshot.EffectivePluginsJsProblem is { } problem)
        {
            return new PatchStatus(PatchStatusKind.PluginsJsUnusable, null, [pluginsError, PatchMessages.DescribePluginsJsUnusableGuidance(problem)]);
        }

        return new PatchStatus(PatchStatusKind.NotApplied, null, ["パッチは適用されていません。"]);
    }

    /// <summary>
    /// 状態ファイルが無い（または壊れている）ときに、パッチの痕跡（壊れた状態ファイル・エントリ・追加ファイル・バックアップ・一時ファイル）を
    /// 根拠の文言として列挙する純粋関数。plugins.js の問題は痕跡に含めない（CA-08）。
    /// </summary>
    private static List<string> FindTracesWithoutState(GameSnapshot snapshot)
    {
        var reasons = new List<string>();
        if (snapshot.StateError is not null)
        {
            reasons.Add(snapshot.StateError);
        }

        if (snapshot.PluginsJsHasEntry)
        {
            reasons.Add("plugins.js にパッチのエントリが残っています。");
        }

        var leftovers = snapshot.PayloadFileHashes.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToList();
        if (leftovers.Count > 0)
        {
            reasons.Add($"パッチのファイルが残っています: {string.Join(", ", leftovers)}");
        }

        if (snapshot.BackupDirectoryExists)
        {
            reasons.Add($"バックアップフォルダ（{GameConstants.BackupDirectoryName}）が残っています。");
        }

        if (snapshot.LeftoverTemporaryFiles.Count > 0)
        {
            reasons.Add($"作業用の一時ファイルが残っています（処理が途中で中断された可能性があります）: {string.Join(", ", snapshot.LeftoverTemporaryFiles)}");
        }

        return reasons;
    }
}

/// <summary>パッチのバージョン文字列（"x.y.z"）の比較。</summary>
public static class PatchVersionComparer
{
    /// <summary>
    /// <paramref name="applied"/> が <paramref name="current"/> より古いかを判定する。
    /// どちらかがバージョン形式として解釈できない場合は、文字列が異なれば「古い」とみなす（更新を促す安全側の判定）。
    /// </summary>
    /// <param name="applied">適用済みのバージョン。</param>
    /// <param name="current">このツールが持つバージョン。</param>
    public static bool IsOlder(string applied, string current)
    {
        var appliedVersion = TryParse(applied);
        var currentVersion = TryParse(current);
        if (appliedVersion is null || currentVersion is null)
        {
            return !string.Equals(applied, current, StringComparison.Ordinal);
        }

        return appliedVersion < currentVersion;
    }

    /// <summary>"x.y.z"（プレリリース等の接尾辞は除く）を Version に変換する。失敗すれば null。</summary>
    private static Version? TryParse(string text)
    {
        var core = text.Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }

        var numbers = parts.Select(p => int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture))
            .Concat(Enumerable.Repeat(0, 4 - parts.Length))
            .ToArray();
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }
}
