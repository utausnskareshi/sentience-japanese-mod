using System.Text;
using SentienceJP.Core.Patching;
using SentienceJP.Core.Plugins;
using SentienceJP.Core.Tests.TestSupport;

namespace SentienceJP.Core.Tests.Patching;

/// <summary>
/// 判定だけを行う純粋関数（plugins.js の戻し方・失敗時の文言・画面の構成）のテスト。ファイルは使わない。
/// </summary>
public sealed class PatchDecisionTests
{
    private static readonly string Original = new('1', 64);
    private static readonly string Patched = new('2', 64);
    private static readonly string Other = new('3', 64);

    [Fact]
    public void Revert_PatchedContentWithValidBackup_UsesBackup()
    {
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Patched, true, null), Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.ReplaceWithOriginal, decision.Method);
    }

    [Theory]
    [InlineData(null, "見つからない")]
    [InlineData("3333333333333333333333333333333333333333333333333333333333333333", "一致しない")]
    public void Revert_PatchedContentWithoutUsableBackup_RemovesEntry(string? backupSha, string reasonFragment)
    {
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Patched, true, null), Original, Patched, backupSha);

        Assert.Equal(PluginsJsRevertMethod.RemoveEntry, decision.Method);
        Assert.Contains(reasonFragment, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_StillOriginalContent_DoesNothing()
    {
        // 置き換えの前に失敗した場合の取り消しや、Steam の整合性チェックで戻された後の復元では、何も書き換えない
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Original, false, null), Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.None, decision.Method);
        Assert.Contains("適用前の内容のまま", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_ChangedByOthersWithEntry_RemovesOnlyEntry()
    {
        // C-01: 適用後に他のプログラムが書き換えていれば、バックアップで上書きせず、エントリだけを取り除く
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Other, true, null), Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.RemoveEntry, decision.Method);
    }

    [Fact]
    public void Revert_ReplacedBySteamWithoutEntry_LeavesAsIs()
    {
        // C-01: Steam の更新で新しい内容（エントリなし）に置き換わっていれば、何もしない（古い内容を書き戻さない）
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Other, false, null), Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.None, decision.Method);
    }

    [Fact]
    public void Revert_UnparsableWithValidBackup_UsesBackup()
    {
        // C-04: 解析できない（破損）場合でも、記録と一致するバックアップがあれば戻せる
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Other, null, "壊れています"), Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.ReplaceWithOriginal, decision.Method);
        Assert.Contains("壊れています", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Revert_UnparsableButNotCorruptWithoutPluginName_LeavesAsIs(bool backupUsable)
    {
        // CA-07: 破損ではない正しいテキスト（厳密な JSON でないだけ）で、プラグイン名を含まなければ、バックアップがあっても上書きしない
        var current = new PluginsJsObservation(Other, null, "末尾カンマ") { LooksCorrupt = false, MentionsPluginName = false };

        var decision = PluginsJsRevertPlanner.Decide(current, Original, Patched, backupUsable ? Original : null);

        Assert.Equal(PluginsJsRevertMethod.None, decision.Method);
        Assert.Contains("そのままにします", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_UnparsableButNotCorruptMentioningPluginName_IsUnrecoverableWithGuidance()
    {
        // CA-07: プラグイン名を含む場合は登録が残っている可能性があるため、自動では戻さず案内する（バックアップでも上書きしない）
        var current = new PluginsJsObservation(Other, null, "末尾カンマ") { LooksCorrupt = false, MentionsPluginName = true };

        var decision = PluginsJsRevertPlanner.Decide(current, Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.Unrecoverable, decision.Method);
        Assert.Contains(GameConstants.PluginName, decision.Reason, StringComparison.Ordinal);
        Assert.Contains("整合性を確認", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_ClassifiesCorruptionAndPluginNameMention()
    {
        // CA-07: 長さ 0・NUL を含む・UTF-8 として不正 は破損らしい。厳密な JSON でないだけの正しいテキストは破損ではない
        var lenient = Encoding.UTF8.GetBytes("var $plugins =\n[\n{\"name\":\"A\"},\n];\n");
        var lenientWithEntry = Encoding.UTF8.GetBytes("var $plugins =\n[\n{\"name\":\"SentienceJP\"},\n];\n");

        Assert.True(PluginsJsObservation.Observe([]).LooksCorrupt);
        Assert.True(PluginsJsObservation.Observe(new byte[16]).LooksCorrupt);
        Assert.True(PluginsJsObservation.Observe([0x76, 0xFF, 0xFE, 0x61]).LooksCorrupt);
        Assert.False(PluginsJsObservation.Observe(lenient).LooksCorrupt);
        Assert.Null(PluginsJsObservation.Observe(lenient).HasEntry);
        Assert.False(PluginsJsObservation.Observe(lenient).MentionsPluginName);
        Assert.True(PluginsJsObservation.Observe(lenientWithEntry).MentionsPluginName);
        Assert.False(PluginsJsObservation.Observe(Encoding.UTF8.GetBytes(FakeGame.SamplePluginsJs)).LooksCorrupt);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Revert_UnparsableWithoutUsableBackup_IsUnrecoverableWithGuidance(bool hasRecord, bool hasBackup)
    {
        var decision = PluginsJsRevertPlanner.Decide(
            new PluginsJsObservation(Other, null, "壊れています"),
            hasRecord ? Original : null,
            hasRecord ? Patched : null,
            hasBackup ? new string('9', 64) : null);

        Assert.Equal(PluginsJsRevertMethod.Unrecoverable, decision.Method);
        Assert.Contains("整合性を確認", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_MissingWithValidBackup_RebuildsFromBackup()
    {
        // CA-05: plugins.js が無い（null）場合、記録と一致するバックアップがあれば作り直す
        var decision = PluginsJsRevertPlanner.Decide(null, Original, Patched, Original);

        Assert.Equal(PluginsJsRevertMethod.ReplaceWithOriginal, decision.Method);
        Assert.Contains("作り直しています", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Revert_MissingWithoutUsableBackup_IsUnrecoverableWithGuidance(bool hasRecord, bool hasMismatchedBackup)
    {
        var decision = PluginsJsRevertPlanner.Decide(
            null,
            hasRecord ? Original : null,
            hasRecord ? Patched : null,
            hasMismatchedBackup ? Other : null);

        Assert.Equal(PluginsJsRevertMethod.Unrecoverable, decision.Method);
        Assert.Contains("plugins.js が無く", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("整合性を確認", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("その編集は消える", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, PluginsJsRevertMethod.RemoveEntry)]
    [InlineData(false, PluginsJsRevertMethod.None)]
    public void Revert_WithoutRecord_DependsOnEntry(bool hasEntry, PluginsJsRevertMethod expected)
    {
        var decision = PluginsJsRevertPlanner.Decide(new PluginsJsObservation(Other, hasEntry, null), null, null, null);

        Assert.Equal(expected, decision.Method);
    }

    [Fact]
    public void Observe_DetectsEntryAndParseErrors()
    {
        var withEntry = Encoding.UTF8.GetBytes(PluginsJsEditor.InsertEntry(FakeGame.SamplePluginsJs, FakeGame.PluginEntryJson));

        Assert.True(PluginsJsObservation.Observe(withEntry).HasEntry);
        Assert.False(PluginsJsObservation.Observe(Encoding.UTF8.GetBytes(FakeGame.SamplePluginsJs)).HasEntry);
        var broken = PluginsJsObservation.Observe(new byte[16]);
        Assert.Null(broken.HasEntry);
        Assert.NotNull(broken.ParseError);
        Assert.Equal(Hashing.Sha256Hex(new byte[16]), broken.Sha256);
    }

    [Fact]
    public void DescribeFailure_WithCleanupErrors_DoesNotClaimRestored()
    {
        // C-09: 取り消しに失敗した場合は「元の状態に戻しました」と伝えない
        var message = PatchApplier.DescribeFailure(new IOException("使用中"), rolledBack: true, ["plugins.js を元に戻す に失敗しました"]);

        Assert.Contains("取り消せませんでした", message, StringComparison.Ordinal);
        Assert.DoesNotContain("元の状態に戻しました", message, StringComparison.Ordinal);
        Assert.Contains("使用中", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_RolledBackCompletely_SaysRestored()
    {
        Assert.Contains("元の状態に戻しました", PatchApplier.DescribeFailure(new IOException("x"), rolledBack: true, []), StringComparison.Ordinal);
        Assert.Contains("元の状態に戻しました", PatchApplier.DescribeFailure(new PatchException("検証に失敗"), rolledBack: true, []), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_BeforeAnyChange_IsJustTheReason()
    {
        Assert.Equal("適用できません", PatchApplier.DescribeFailure(new PatchException("適用できません"), rolledBack: false, []));
        Assert.Contains("開始できませんでした", PatchApplier.DescribeFailure(new IOException("x"), rolledBack: false, []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PatchStatusKind.NotApplied, PatchAction.Apply, false)]
    [InlineData(PatchStatusKind.Applied, PatchAction.Restore, false)]
    [InlineData(PatchStatusKind.NeedsReapply, PatchAction.Reapply, true)]
    [InlineData(PatchStatusKind.Outdated, PatchAction.Update, true)]
    [InlineData(PatchStatusKind.Inconsistent, PatchAction.Restore, false)]
    public void Screen_KnownStatus_MapsToButtons(PatchStatusKind kind, PatchAction action, bool secondary)
    {
        var plan = PatchScreenPlanner.Plan(new PatchScreenInput(new PatchStatus(kind, "0.1.0", ["理由1", "理由2"]), null, false));

        Assert.Equal(action, plan.MainButton.Action);
        Assert.False(plan.MainButton.IsRecheck);
        Assert.True(plan.MainButton.Enabled);
        Assert.Equal(secondary, plan.ShowSecondaryRestore);
        Assert.Equal(secondary, plan.SecondaryRestoreEnabled);
        Assert.Equal(PatchOperations.PlanFor(new PatchStatus(kind, "0.1.0", [])).StatusLabel, plan.StatusText);
        Assert.Equal($"理由1{Environment.NewLine}理由2", plan.StatusDetail);
    }

    [Theory]
    [InlineData(PatchStatusKind.NewerVersionApplied)]
    [InlineData(PatchStatusKind.PluginsJsUnusable)]
    [InlineData(PatchStatusKind.GameNotFound)]
    public void Screen_StatusWithoutAvailableOperation_OffersOnlyRecheck(PatchStatusKind kind)
    {
        // CA-04 ほか: このツールで行える操作が無い状態では、主ボタンを「再確認する」（Action=null）にし、補助の「元に戻す」も出さない
        var status = new PatchStatus(kind, "0.3.0", ["理由"]);

        var plan = PatchScreenPlanner.Plan(new PatchScreenInput(status, null, false));
        var busy = PatchScreenPlanner.Plan(new PatchScreenInput(status, null, true));

        Assert.True(plan.MainButton.IsRecheck);
        Assert.Equal(PatchScreenPlanner.RecheckLabel, plan.MainButton.Label);
        Assert.True(plan.MainButton.Enabled);
        Assert.False(plan.ShowSecondaryRestore);
        Assert.False(busy.MainButton.Enabled);
        Assert.All(Enum.GetValues<PatchAction>(), action => Assert.False(PatchScreenPlanner.IsActionAvailable(status, action)));
    }

    [Fact]
    public void Screen_Busy_DisablesButtons()
    {
        var plan = PatchScreenPlanner.Plan(new PatchScreenInput(new PatchStatus(PatchStatusKind.Outdated, "0.0.9", []), null, true));

        Assert.False(plan.MainButton.Enabled);
        Assert.True(plan.ShowSecondaryRestore);
        Assert.False(plan.SecondaryRestoreEnabled);
    }

    [Fact]
    public void Screen_CheckFailed_OffersRecheck()
    {
        // A-04: 状態の確認に失敗しても、「再確認する」で確認し直せる（ボタンが押せないままにならない）
        var plan = PatchScreenPlanner.Plan(new PatchScreenInput(null, "アクセスが拒否されました。", false));

        Assert.True(plan.MainButton.IsRecheck);
        Assert.True(plan.MainButton.Enabled);
        Assert.Equal(PatchScreenPlanner.RecheckLabel, plan.MainButton.Label);
        Assert.Equal("アクセスが拒否されました。", plan.StatusDetail);
        Assert.False(plan.ShowSecondaryRestore);
    }

    [Fact]
    public void Screen_BeforeFirstCheck_IsDisabled()
    {
        var plan = PatchScreenPlanner.Plan(new PatchScreenInput(null, null, false));

        Assert.Equal(PatchScreenPlanner.CheckingLabel, plan.MainButton.Label);
        Assert.False(plan.MainButton.Enabled);
        Assert.False(plan.SecondaryRestoreEnabled);
    }

    [Theory]
    [InlineData(PatchStatusKind.NotApplied, PatchAction.Apply, true)]
    [InlineData(PatchStatusKind.NotApplied, PatchAction.Restore, false)]
    [InlineData(PatchStatusKind.Applied, PatchAction.Apply, false)]
    [InlineData(PatchStatusKind.NeedsReapply, PatchAction.Restore, true)]
    [InlineData(PatchStatusKind.NeedsReapply, PatchAction.Update, false)]
    [InlineData(PatchStatusKind.Outdated, PatchAction.Reapply, false)]
    [InlineData(PatchStatusKind.Inconsistent, PatchAction.Restore, true)]
    public void IsActionAvailable_ChecksAgainstLatestStatus(PatchStatusKind kind, PatchAction action, bool expected)
    {
        // A-08: 実行直前に取り直した状態で、押された操作がまだ選べるかを確認する
        Assert.Equal(expected, PatchScreenPlanner.IsActionAvailable(new PatchStatus(kind, "0.1.0", []), action));
    }

    [Theory]
    [InlineData(PatchAction.Reapply, PatchStatusKind.NotApplied, true)]
    [InlineData(PatchAction.Update, PatchStatusKind.NotApplied, true)]
    [InlineData(PatchAction.Update, PatchStatusKind.Outdated, false)]
    [InlineData(PatchAction.Apply, PatchStatusKind.NotApplied, false)]
    [InlineData(PatchAction.Restore, PatchStatusKind.NotApplied, false)]
    public void FailureFollowUp_TellsWhenReapplyLeftGameInEnglish(PatchAction action, PatchStatusKind after, bool expected)
    {
        // A-09: 「再適用」「更新」で元に戻した後の日本語化だけが失敗した場合、英語に戻っていることを伝える
        var followUp = PatchScreenPlanner.DescribeFailureFollowUp(action, new PatchStatus(after, null, []));

        Assert.Equal(expected, followUp is not null);
        Assert.Null(PatchScreenPlanner.DescribeFailureFollowUp(action, null));
    }
}
