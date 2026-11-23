using SentienceJP.Core.Payload;

namespace SentienceJP.Core.Patching;

/// <summary>利用者が選べる操作の種類。</summary>
public enum PatchAction
{
    /// <summary>日本語化する（未適用 → 適用）。</summary>
    Apply,

    /// <summary>元に戻す（適用済み・不整合 → 未適用）。</summary>
    Restore,

    /// <summary>再適用する（要再適用 → 適用）。</summary>
    Reapply,

    /// <summary>更新する（旧版 → 新版を適用）。</summary>
    Update,
}

/// <summary>画面に表示するボタン構成。</summary>
/// <param name="MainAction">
/// メインボタンの操作。null のときは、このツールで行える操作が無いため「状態を確認し直す」（再確認）を行う
/// （新しいバージョンのツールで適用されている・plugins.js を使えない・ゲームフォルダが無い、など）。
/// </param>
/// <param name="MainLabel">メインボタンの表示文字列。</param>
/// <param name="ShowSecondaryRestore">補助の「元に戻す」ボタンを表示するか。</param>
/// <param name="StatusLabel">状態の表示文字列。</param>
public sealed record PatchUiPlan(PatchAction? MainAction, string MainLabel, bool ShowSecondaryRestore, string StatusLabel);

/// <summary>
/// 状態から画面の操作を決める純粋関数と、複合操作（再適用・更新）の実行。
/// </summary>
public static class PatchOperations
{
    /// <summary>
    /// 状態に応じたボタン構成を決める。
    /// </summary>
    /// <param name="status">現在の適用状態。</param>
    public static PatchUiPlan PlanFor(PatchStatus status) => status.Kind switch
    {
        PatchStatusKind.NotApplied => new PatchUiPlan(PatchAction.Apply, "日本語化する", false, "未適用（英語）"),
        PatchStatusKind.Applied => new PatchUiPlan(PatchAction.Restore, "元に戻す", false, $"適用済み（日本語） v{status.AppliedPatchVersion}"),
        PatchStatusKind.NeedsReapply => new PatchUiPlan(PatchAction.Reapply, "再適用する", true, "再適用が必要です（Steam の更新や整合性チェックで元に戻された可能性があります）"),
        PatchStatusKind.Outdated => new PatchUiPlan(PatchAction.Update, "更新する", true, $"旧バージョン（v{status.AppliedPatchVersion}）が適用されています"),
        PatchStatusKind.Inconsistent => new PatchUiPlan(PatchAction.Restore, "元に戻す（クリーンアップ）", false, "不整合（パッチのファイルが一部残っています）"),

        // 以下は、このツールで行える操作が無い状態。主ボタンは「再確認する」にし、案内は状態の詳細（判定の根拠）で伝える
        PatchStatusKind.NewerVersionApplied => new PatchUiPlan(
            null,
            PatchScreenPlanner.RecheckLabel,
            false,
            status.AppliedPatchVersion is { } newer ? $"新しいバージョン（v{newer}）のツールで適用されています" : "新しいバージョンのツールで適用されています"),
        PatchStatusKind.PluginsJsUnusable => new PatchUiPlan(null, PatchScreenPlanner.RecheckLabel, false, "未適用（plugins.js に問題があるため、日本語化できません）"),
        PatchStatusKind.GameNotFound => new PatchUiPlan(null, PatchScreenPlanner.RecheckLabel, false, "ゲームフォルダが見つかりません（ツールを起動し直してください）"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status.Kind, "未知の状態です。"),
    };

    /// <summary>
    /// 操作を実行する。再適用・更新は「復元 → 適用」の組み合わせで行う。
    /// </summary>
    /// <param name="action">実行する操作。</param>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="gameBuildId">ゲームのビルドID。</param>
    /// <param name="payload">検証済みのペイロード。</param>
    /// <param name="log">進行状況の通知先。</param>
    public static PatchOperationResult Execute(
        PatchAction action,
        string gameDirectory,
        string? gameBuildId,
        PayloadBundle payload,
        Action<string> log) => action switch
    {
        PatchAction.Apply => PatchApplier.Apply(gameDirectory, gameBuildId, payload, log),
        PatchAction.Restore => PatchRestorer.Restore(gameDirectory, payload.Manifest, log),
        PatchAction.Reapply or PatchAction.Update => RestoreThenApply(gameDirectory, gameBuildId, payload, log),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知の操作です。"),
    };

    /// <summary>復元してから適用する。復元に失敗した場合は適用しない。</summary>
    private static PatchOperationResult RestoreThenApply(string gameDirectory, string? gameBuildId, PayloadBundle payload, Action<string> log)
    {
        log("いったん元の状態に戻してから、適用し直します。");
        var restored = PatchRestorer.Restore(gameDirectory, payload.Manifest, log);
        return restored.Success
            ? PatchApplier.Apply(gameDirectory, gameBuildId, payload, log)
            : restored;
    }
}
