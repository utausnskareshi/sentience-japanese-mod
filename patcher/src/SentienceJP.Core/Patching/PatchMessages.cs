namespace SentienceJP.Core.Patching;

/// <summary>
/// 状態判定・適用・復元・画面で共通に使う、利用者向けの案内文（純粋関数と定数）。
/// 同じ状況を、状態の詳細・中止のメッセージ・画面のダイアログで同じ言葉で伝えるため、ここに集める。
/// </summary>
public static class PatchMessages
{
    /// <summary>
    /// plugins.js を Steam の「ゲームファイルの整合性を確認」で修復する案内（CA-05・CA-08）。
    /// 整合性の確認は plugins.js を公式の内容に戻すため、他の MOD などによる編集が消えることも伝える。
    /// </summary>
    public const string SteamVerifyGuidance =
        "Steam の「ゲームファイルの整合性を確認」を実行して plugins.js を修復してから、もう一度実行してください"
        + "（他の MOD などで plugins.js を編集していた場合、その編集は消えるため、必要なら内容を控えておいてください）。";

    /// <summary>plugins.js が（ロック・アクセス拒否で）読めない場合の案内（CA-05・CA-08）。</summary>
    public const string PluginsJsUnreadableGuidance =
        "plugins.js を開いているプログラム（エディタなど）を閉じるか、ファイルのアクセス許可を確認してから、もう一度実行してください。";

    /// <summary>
    /// 起動時に検出したゲームフォルダが無くなった場合の案内（AA-08）。
    /// 操作は行わず（副作用なし）、起動し直して検出し直すよう伝える。
    /// </summary>
    public const string GameNotFoundGuidance =
        "ゲームフォルダが見つかりません（移動・アンインストールされた可能性があります）。ツールを起動し直してください。";

    /// <summary>新しいバージョンのツールで適用されている場合の案内（CA-04）。</summary>
    public const string NewerVersionGuidance =
        "新しいバージョンのツールで適用されています。そのツールで「元に戻す」を実行してください（このツールでは、ゲームのファイルを変更しません）。";

    /// <summary>
    /// 状態ファイルがこのバージョンでは扱えない場合に、元に戻すを中止するときの説明（CA-04）。
    /// 何も変更していないこと・どの記録が扱えないか・新しいバージョンのツールで元に戻すことを伝える。
    /// </summary>
    /// <param name="unsupported">扱えない状態ファイルの情報。</param>
    public static string DescribeRestoreBlockedByNewerVersion(UnsupportedPatchState unsupported)
    {
        var newLine = Environment.NewLine;
        return $"このバージョンのツールでは元に戻せない変更が記録されています（{unsupported.Summary}）。{newLine}"
            + $"新しいバージョンのツールで適用されています。そのツールで「元に戻す」を実行してください。{newLine}"
            + "（ゲームのファイルとバックアップは変更していません）";
    }

    /// <summary>
    /// 状態ファイルがある（「元に戻す」で片付ける）状況で plugins.js を使えない場合に、理由に応じて「元に戻す」で何が起きるかを伝える案内（CA-05）。
    /// </summary>
    /// <param name="problem">plugins.js を使えない理由の種類。</param>
    public static string DescribeRestoreHintForPluginsJs(PluginsJsProblemKind problem) => problem switch
    {
        PluginsJsProblemKind.Missing =>
            "「元に戻す」を実行すると、記録と一致するバックアップから plugins.js を作り直します。"
            + "バックアップが無い・一致しない場合は、Steam の「ゲームファイルの整合性を確認」で修復してください。",
        PluginsJsProblemKind.Unreadable => PluginsJsUnreadableGuidance,
        _ => "「元に戻す」を実行すると、plugins.js が破損していれば、記録と一致するバックアップから適用前の内容に戻します（他のプログラムが書き換えた内容は残します）。",
    };

    /// <summary>
    /// 未適用で plugins.js を使えない状態（<see cref="PatchStatusKind.PluginsJsUnusable"/>）の、理由に応じた対処の案内（CA-08）。
    /// 主ボタンは「再確認する」なので、対処の後に押すよう伝える。整合性の確認を勧める場合は、他の MOD などの編集が消えることも添える。
    /// </summary>
    /// <param name="problem">plugins.js を使えない理由の種類。</param>
    public static string DescribePluginsJsUnusableGuidance(PluginsJsProblemKind problem) => problem switch
    {
        PluginsJsProblemKind.Missing =>
            "Steam の「ゲームファイルの整合性を確認」を実行して plugins.js を修復してから、「再確認する」を押してください"
            + "（他の MOD などで plugins.js を編集していた場合、その編集は消えます）。",
        PluginsJsProblemKind.Unreadable =>
            "plugins.js を開いているプログラム（エディタなど）を閉じるか、ファイルのアクセス許可を確認してから、「再確認する」を押してください。",
        _ =>
            "このツールが想定する形式（RPGツクールMV が書き出す形式）ではないため、日本語化できません。"
            + "他の MOD などで plugins.js を編集している場合は、その内容を確認してください。"
            + "Steam の「ゲームファイルの整合性を確認」を実行すると元の形式に戻りますが、他の MOD などによる plugins.js の編集は消えます。"
            + "修復したら「再確認する」を押してください。",
    };

    /// <summary>
    /// plugins.js を使えないために日本語化を中止するときの説明（CA-05・CA-08）。理由（無い・読めない・解析できない）ごとに案内を変える。
    /// </summary>
    /// <param name="problem">plugins.js を使えない理由の種類。</param>
    /// <param name="error">状態判定で得た具体的な説明。</param>
    public static string DescribeApplyBlockedByPluginsJs(PluginsJsProblemKind problem, string error)
    {
        var newLine = Environment.NewLine;
        return problem switch
        {
            PluginsJsProblemKind.Missing => $"plugins.js が無いため、日本語化できません（{error}）。{newLine}{SteamVerifyGuidance}",
            PluginsJsProblemKind.Unreadable => $"plugins.js を読み取れないため、日本語化できません（{error}）。{newLine}{PluginsJsUnreadableGuidance}",
            _ => $"plugins.js を解析できないため、日本語化できません（{error}）。{newLine}{SteamVerifyGuidance}",
        };
    }

    /// <summary>
    /// 状態ファイルがこのバージョンでは扱えない場合に、日本語化を中止するときの説明（CA-04）。
    /// </summary>
    /// <param name="unsupported">扱えない状態ファイルの情報。</param>
    public static string DescribeApplyBlockedByNewerVersion(UnsupportedPatchState unsupported)
    {
        var newLine = Environment.NewLine;
        return $"新しいバージョンのツールで適用されています（{unsupported.Summary}）。{newLine}"
            + $"そのツールで「元に戻す」を実行してから、日本語化してください。{newLine}"
            + "（ゲームのファイルは変更していません）";
    }
}
