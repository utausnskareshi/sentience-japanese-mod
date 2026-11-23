using SentienceJP.Core.Patching;
using SentienceJP.Core.Payload;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// 状態判定（純粋関数）のテスト。ファイルを使わず、スナップショットを直接組み立てて検証する。
/// </summary>
public sealed class PatchStatusDetectorTests
{
    private const string FileA = "www/js/plugins/SentienceJP.js";
    private const string FileB = "www/sentience_jp/translation.json";
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private static readonly PayloadManifest Manifest = new(
        1,
        "0.2.0",
        "t",
        GameConstants.PluginName,
        "{}",
        [new PayloadFileEntry(FileA, HashA, 1), new PayloadFileEntry(FileB, HashB, 1)],
        [],
        []);

    [Fact]
    public void NoStateAndNoTraces_IsNotApplied()
    {
        var status = PatchStatusDetector.Detect(Snapshot(), Manifest);

        Assert.Equal(PatchStatusKind.NotApplied, status.Kind);
    }

    [Fact]
    public void NoStateButEntry_IsInconsistent()
    {
        var status = PatchStatusDetector.Detect(Snapshot(hasEntry: true), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
    }

    [Fact]
    public void NoStateButLeftoverFile_IsInconsistent()
    {
        var status = PatchStatusDetector.Detect(Snapshot(fileA: HashA), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Contains(status.Reasons, r => r.Contains(FileA, StringComparison.Ordinal));
    }

    [Fact]
    public void NoStateButBackupFolder_IsInconsistent()
    {
        var status = PatchStatusDetector.Detect(Snapshot(backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
    }

    [Fact]
    public void CorruptState_IsInconsistent()
    {
        var status = PatchStatusDetector.Detect(Snapshot(stateError: "壊れています", backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Contains("壊れています", status.Reasons);
    }

    [Fact]
    public void StateWithEverythingInPlace_IsApplied()
    {
        var status = PatchStatusDetector.Detect(Snapshot(state: State("0.2.0"), hasEntry: true, fileA: HashA, fileB: HashB, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.Applied, status.Kind);
        Assert.Equal("0.2.0", status.AppliedPatchVersion);
    }

    [Fact]
    public void StateNewerThanTool_IsApplied()
    {
        var status = PatchStatusDetector.Detect(Snapshot(state: State("0.3.0"), hasEntry: true, fileA: HashA, fileB: HashB, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.Applied, status.Kind);
    }

    [Fact]
    public void StateButEntryRemovedBySteam_NeedsReapply()
    {
        var status = PatchStatusDetector.Detect(Snapshot(state: State("0.2.0"), hasEntry: false, fileA: HashA, fileB: HashB, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.NeedsReapply, status.Kind);
    }

    [Fact]
    public void StateButFileMissingOrChanged_NeedsReapply()
    {
        var missing = PatchStatusDetector.Detect(Snapshot(state: State("0.2.0"), hasEntry: true, fileA: HashA, fileB: null, backupExists: true), Manifest);
        var changed = PatchStatusDetector.Detect(Snapshot(state: State("0.2.0"), hasEntry: true, fileA: HashA, fileB: HashA, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.NeedsReapply, missing.Kind);
        Assert.Equal(PatchStatusKind.NeedsReapply, changed.Kind);
    }

    [Fact]
    public void OlderState_IsOutdated()
    {
        var status = PatchStatusDetector.Detect(Snapshot(state: State("0.1.9"), hasEntry: true, fileA: HashA, fileB: HashB, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.Outdated, status.Kind);
        Assert.Equal("0.1.9", status.AppliedPatchVersion);
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.10.0", "0.9.0", false)]
    [InlineData("1.0", "1.0.1", true)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("abc", "0.1.0", true)]
    [InlineData("abc", "abc", false)]
    public void PatchVersionComparer_IsOlder(string applied, string current, bool expected)
    {
        Assert.Equal(expected, PatchVersionComparer.IsOlder(applied, current));
    }

    [Theory]
    [InlineData(PatchStatusKind.NotApplied, PatchAction.Apply, "日本語化する", false)]
    [InlineData(PatchStatusKind.Applied, PatchAction.Restore, "元に戻す", false)]
    [InlineData(PatchStatusKind.NeedsReapply, PatchAction.Reapply, "再適用する", true)]
    [InlineData(PatchStatusKind.Outdated, PatchAction.Update, "更新する", true)]
    [InlineData(PatchStatusKind.Inconsistent, PatchAction.Restore, "元に戻す（クリーンアップ）", false)]
    public void PlanFor_MapsStatusToButtons(PatchStatusKind kind, PatchAction action, string label, bool secondary)
    {
        var plan = PatchOperations.PlanFor(new PatchStatus(kind, "0.1.0", []));

        Assert.Equal(action, plan.MainAction);
        Assert.Equal(label, plan.MainLabel);
        Assert.Equal(secondary, plan.ShowSecondaryRestore);
    }

    [Fact]
    public void StateButPluginsJsCorrupt_IsInconsistent_AndRestoreIsMainAction()
    {
        // plugins.js が壊れている（解析できない）と再適用もできないため、バックアップから戻す「元に戻す」を主操作にする（C-04）
        var status = PatchStatusDetector.Detect(
            Snapshot(state: State("0.2.0"), hasEntry: false, fileA: HashA, fileB: HashB, backupExists: true, pluginsJsError: "plugins.js が UTF-8 として読み取れません。"),
            Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Contains("plugins.js が UTF-8 として読み取れません。", status.Reasons);
        Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);
    }

    [Fact]
    public void StateWithoutEntryAndFiles_IsInconsistent_NotNeedsReapply()
    {
        // 元に戻す処理が最後のバックアップ削除で止まった状況（状態ファイルとバックアップだけが残る）は、再適用ではなく片付けを促す（C-10）
        var status = PatchStatusDetector.Detect(Snapshot(state: State("0.2.0"), hasEntry: false, fileA: null, fileB: null, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);
    }

    [Fact]
    public void StateWithoutEntryButOneFileLeft_StillNeedsReapply()
    {
        // 追加ファイルが 1 つでも残っていれば、Steam の整合性チェックで plugins.js だけ戻された状況とみなす（従来どおり）
        var status = PatchStatusDetector.Detect(Snapshot(state: State("0.2.0"), hasEntry: false, fileA: HashA, fileB: null, backupExists: true), Manifest);

        Assert.Equal(PatchStatusKind.NeedsReapply, status.Kind);
    }

    [Fact]
    public void NoStateButLeftoverTemporaryFile_IsInconsistent()
    {
        // 適用中の plugins.js 置き換えで中断し、一時ファイルだけが残った状況（C-06）
        var status = PatchStatusDetector.Detect(Snapshot(leftoverTemporaryFiles: ["www/js/plugins.js.sentience_jp.tmp"]), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Contains(status.Reasons, r => r.Contains("www/js/plugins.js.sentience_jp.tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PluginsJsProblemKind.Missing, "バックアップから plugins.js を作り直します")]
    [InlineData(PluginsJsProblemKind.Unreadable, "開いているプログラム")]
    [InlineData(PluginsJsProblemKind.Unparsable, "破損していれば")]
    public void StateButPluginsJsUnusable_ReasonDependsOnProblemKind(PluginsJsProblemKind problem, string expectedHint)
    {
        // CA-05: plugins.js を使えない理由（無い・読めない・解析できない）ごとに、「元に戻す」で何が起きるかの文言を分ける
        var snapshot = Snapshot(state: State("0.2.0"), hasEntry: false, fileA: HashA, fileB: HashB, backupExists: true, pluginsJsError: "plugins.js の問題")
            with { PluginsJsProblem = problem };

        var status = PatchStatusDetector.Detect(snapshot, Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Equal(PatchAction.Restore, PatchOperations.PlanFor(status).MainAction);
        Assert.Equal("plugins.js の問題", status.Reasons[0]);
        Assert.Contains(status.Reasons, r => r.Contains(expectedHint, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PluginsJsProblemKind.Missing, "整合性を確認")]
    [InlineData(PluginsJsProblemKind.Unreadable, "開いているプログラム")]
    [InlineData(PluginsJsProblemKind.Unparsable, "想定する形式")]
    public void NoStateNoTracesButPluginsJsProblem_IsPluginsJsUnusable(PluginsJsProblemKind problem, string expectedGuidance)
    {
        // CA-08: 状態ファイルも痕跡も無く、根拠が plugins.js の問題だけなら「不整合」ではなく専用の状態（主ボタンは再確認）
        var snapshot = Snapshot(pluginsJsError: "plugins.js の問題") with { PluginsJsProblem = problem };

        var status = PatchStatusDetector.Detect(snapshot, Manifest);

        Assert.Equal(PatchStatusKind.PluginsJsUnusable, status.Kind);
        Assert.Equal("plugins.js の問題", status.Reasons[0]);
        Assert.Contains(status.Reasons, r => r.Contains(expectedGuidance, StringComparison.Ordinal));
        Assert.Null(PatchOperations.PlanFor(status).MainAction);
    }

    [Fact]
    public void NoStateWithTracesAndPluginsJsProblem_IsStillInconsistent()
    {
        var status = PatchStatusDetector.Detect(Snapshot(fileA: HashA, pluginsJsError: "plugins.js の問題"), Manifest);

        Assert.Equal(PatchStatusKind.Inconsistent, status.Kind);
        Assert.Contains("plugins.js の問題", status.Reasons);
    }

    [Fact]
    public void EffectivePluginsJsProblem_DefaultsToUnparsableOnlyWhenThereIsAnError()
    {
        Assert.Null(Snapshot().EffectivePluginsJsProblem);
        Assert.Equal(PluginsJsProblemKind.Unparsable, Snapshot(pluginsJsError: "壊れています").EffectivePluginsJsProblem);
        Assert.Equal(PluginsJsProblemKind.Missing, (Snapshot(pluginsJsError: "ありません") with { PluginsJsProblem = PluginsJsProblemKind.Missing }).EffectivePluginsJsProblem);
    }

    [Fact]
    public void MissingGame_IsGameNotFound_BeforeAnythingElse()
    {
        // AA-08: ゲームフォルダが無くなっていれば、他の根拠に関わらず「見つからない」（主ボタンは再確認）
        var snapshot = GameSnapshot.ForMissingGame(@"C:\Game", "ゲームフォルダがありません: C:\\Game");

        var status = PatchStatusDetector.Detect(snapshot, Manifest);
        var plan = PatchOperations.PlanFor(status);

        Assert.Equal(PatchStatusKind.GameNotFound, status.Kind);
        Assert.Equal(["ゲームフォルダがありません: C:\\Game", PatchMessages.GameNotFoundGuidance], status.Reasons);
        Assert.Null(plan.MainAction);
        Assert.Contains("ゲームフォルダが見つかりません", plan.StatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedState_IsNewerVersionApplied_AndOffersOnlyRecheck()
    {
        // CA-04: 新しいバージョンのツールで適用された状態ファイルは、エントリやファイルの有無に関わらず「新しいバージョンで適用済み」
        var unsupported = new UnsupportedPatchState("状態ファイルの形式（schema=2）が、このツールの対応する形式（schema=1）より新しいです。", "0.3.0", []);
        var snapshot = Snapshot(hasEntry: true, fileA: HashA, fileB: HashB, backupExists: true) with { UnsupportedState = unsupported };

        var status = PatchStatusDetector.Detect(snapshot, Manifest);
        var plan = PatchOperations.PlanFor(status);

        Assert.Equal(PatchStatusKind.NewerVersionApplied, status.Kind);
        Assert.Equal("0.3.0", status.AppliedPatchVersion);
        Assert.Contains(unsupported.Reason, status.Reasons);
        Assert.Contains(PatchMessages.NewerVersionGuidance, status.Reasons);
        Assert.Null(plan.MainAction);
        Assert.Equal(PatchScreenPlanner.RecheckLabel, plan.MainLabel);
        Assert.False(plan.ShowSecondaryRestore);
        Assert.Equal("新しいバージョン（v0.3.0）のツールで適用されています", plan.StatusLabel);
    }

    [Fact]
    public void UnsupportedStateWithoutVersion_StatusLabelOmitsVersion()
    {
        var snapshot = Snapshot(backupExists: true) with { UnsupportedState = new UnsupportedPatchState("理由", null, ["www/fonts/a.ttf"]) };

        var plan = PatchOperations.PlanFor(PatchStatusDetector.Detect(snapshot, Manifest));

        Assert.Equal("新しいバージョンのツールで適用されています", plan.StatusLabel);
    }

    /// <summary>テスト用のスナップショットを作る。</summary>
    private static GameSnapshot Snapshot(
        PatchStateFile? state = null,
        string? stateError = null,
        bool hasEntry = false,
        string? fileA = null,
        string? fileB = null,
        bool backupExists = false,
        string? pluginsJsError = null,
        IReadOnlyList<string>? leftoverTemporaryFiles = null) =>
        new(
            @"C:\Game",
            state,
            stateError,
            new string('c', 64),
            hasEntry,
            pluginsJsError,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [FileA] = fileA, [FileB] = fileB },
            backupExists,
            leftoverTemporaryFiles ?? []);

    /// <summary>テスト用の状態ファイルの内容を作る。</summary>
    private static PatchStateFile State(string version) =>
        new(
            1,
            version,
            "t",
            DateTimeOffset.UnixEpoch,
            "2476493",
            [new AddedFileRecord(FileA, HashA), new AddedFileRecord(FileB, HashB)],
            [new ModifiedFileRecord(GameConstants.PluginsJsRelativePath, new string('d', 64), new string('c', 64), "sentience_jp_backup/original/www/js/plugins.js")],
            ["www/sentience_jp"]);
}
