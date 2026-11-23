namespace SentienceJP.Core.Patching;

/// <summary>
/// 画面の表示を決めるための入力。
/// </summary>
/// <param name="Status">判定した適用状態（まだ確認していない、または確認に失敗した場合は null）。</param>
/// <param name="CheckError">状態の確認に失敗した場合のエラー内容（確認前・成功時は null）。</param>
/// <param name="IsBusy">処理を実行中か。</param>
public sealed record PatchScreenInput(PatchStatus? Status, string? CheckError, bool IsBusy);

/// <summary>主ボタンの構成。</summary>
/// <param name="Label">表示文字列。</param>
/// <param name="Action">押したときに実行するパッチ操作。null のときは「状態を確認し直す」を行う。</param>
/// <param name="Enabled">押せるか。</param>
public sealed record MainButtonPlan(string Label, PatchAction? Action, bool Enabled)
{
    /// <summary>押したときに状態の確認し直し（再確認）を行うボタンか。</summary>
    public bool IsRecheck => Action is null;
}

/// <summary>画面に表示する内容とボタンの構成。</summary>
/// <param name="StatusText">状態の表示。</param>
/// <param name="StatusDetail">状態の詳細（判定の根拠やエラー内容）。</param>
/// <param name="MainButton">主ボタン。</param>
/// <param name="ShowSecondaryRestore">補助の「元に戻す」ボタンを表示するか。</param>
/// <param name="SecondaryRestoreEnabled">補助の「元に戻す」ボタンを押せるか。</param>
public sealed record PatchScreenPlan(
    string StatusText,
    string StatusDetail,
    MainButtonPlan MainButton,
    bool ShowSecondaryRestore,
    bool SecondaryRestoreEnabled);

/// <summary>
/// 「状態 → 画面の表示・ボタンが実行する操作」を決める純粋関数群。
/// 画面（ViewModel）はこの結果をそのまま表示に反映するだけにし、判断はここに集める（単体テストで確認できるようにするため）。
/// </summary>
public static class PatchScreenPlanner
{
    /// <summary>状態の確認に失敗したときの主ボタンの表示。</summary>
    public const string RecheckLabel = "再確認する";

    /// <summary>状態を確認している間の表示。</summary>
    public const string CheckingLabel = "確認中…";

    /// <summary>
    /// 画面の表示とボタンの構成を決める。
    /// - 状態が分かっている: <see cref="PatchOperations.PlanFor"/> の構成。処理中はボタンを押せない。
    /// - 確認に失敗した: 主ボタンを「再確認する」にする（失敗したままボタンが押せなくなるのを防ぐ）。
    /// - まだ確認していない: 「確認中…」で、ボタンは押せない。
    /// </summary>
    /// <param name="input">画面の入力。</param>
    public static PatchScreenPlan Plan(PatchScreenInput input)
    {
        if (input.Status is { } status)
        {
            var plan = PatchOperations.PlanFor(status);
            return new PatchScreenPlan(
                plan.StatusLabel,
                string.Join(Environment.NewLine, status.Reasons),
                new MainButtonPlan(plan.MainLabel, plan.MainAction, !input.IsBusy),
                plan.ShowSecondaryRestore,
                plan.ShowSecondaryRestore && !input.IsBusy);
        }

        if (input.CheckError is { } error)
        {
            return new PatchScreenPlan(
                "状態を確認できませんでした",
                error,
                new MainButtonPlan(RecheckLabel, null, !input.IsBusy),
                false,
                false);
        }

        return new PatchScreenPlan(CheckingLabel, string.Empty, new MainButtonPlan(CheckingLabel, null, false), false, false);
    }

    /// <summary>
    /// 押された操作が、最新の状態でも選べる操作か（主ボタンの操作、または補助の「元に戻す」）を判定する。
    /// 確認ダイアログの間などに状態が変わっていた場合に、古い画面のまま実行してしまうのを防ぐために使う。
    /// </summary>
    /// <param name="status">実行直前に取り直した状態。</param>
    /// <param name="action">押された操作。</param>
    public static bool IsActionAvailable(PatchStatus status, PatchAction action)
    {
        var plan = PatchOperations.PlanFor(status);
        return plan.MainAction == action || (action == PatchAction.Restore && plan.ShowSecondaryRestore);
    }

    /// <summary>
    /// 操作に失敗した後、利用者に補足すべきことがあれば返す（無ければ null）。
    /// 「再適用」「更新」は「元に戻す → 日本語化」の組み合わせのため、元に戻した後の日本語化だけが失敗すると英語の状態になる。
    /// そのことが伝わるよう、失敗後の状態が未適用なら補足する。
    /// </summary>
    /// <param name="action">失敗した操作。</param>
    /// <param name="statusAfter">失敗後に取り直した状態（取得できなかった場合は null）。</param>
    public static string? DescribeFailureFollowUp(PatchAction action, PatchStatus? statusAfter) =>
        action is PatchAction.Reapply or PatchAction.Update && statusAfter?.Kind == PatchStatusKind.NotApplied
            ? "以前のパッチは取り除かれ、現在は英語の状態に戻っています。原因を取り除いてから「日本語化する」を実行してください。"
            : null;
}
