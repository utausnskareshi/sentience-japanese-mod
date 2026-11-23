using SentienceJP.Core.Plugins;

namespace SentienceJP.Core.Patching;

/// <summary>
/// plugins.js の現在の様子（元に戻す方法を決めるための入力）。
/// </summary>
/// <param name="Sha256">現在の内容の SHA-256。</param>
/// <param name="HasEntry">パッチのエントリがあるか。解析できなかった場合は null。</param>
/// <param name="ParseError">解析できなかった場合の理由（解析できた場合は null）。</param>
public sealed record PluginsJsObservation(string Sha256, bool? HasEntry, string? ParseError)
{
    /// <summary>
    /// 解析できなかった内容が「破損らしい」か（長さ 0・NUL を含む・UTF-8 として不正。電源断やディスクの障害を想定。CA-07）。
    /// 破損らしい場合だけ、記録と一致するバックアップで上書きして戻す。厳密な JSON でないだけの正しいテキスト
    /// （Steam の更新や他の MOD が書いた新しい plugins.js など）は上書きしない。
    /// <see cref="Observe"/> 以外で作った場合（判定材料が無い場合）は、従来どおり破損として扱う（既定値 true）。
    /// </summary>
    public bool LooksCorrupt { get; init; } = true;

    /// <summary>
    /// 内容にプラグイン名（<see cref="GameConstants.PluginName"/>）の文字列が含まれるか（CA-07）。
    /// 解析できない内容にパッチの登録が残っている可能性があるかの判断に使う。
    /// <see cref="Observe"/> 以外で作った場合は、残っている可能性があるものとして扱う（既定値 true。安全側）。
    /// </summary>
    public bool MentionsPluginName { get; init; } = true;

    /// <summary>
    /// plugins.js の内容を調べる純粋関数（UTF-8 として可逆に読めて、$plugins 配列を解析できるか・エントリがあるか、
    /// 解析できない場合は破損らしいか・プラグイン名を含むか）。
    /// </summary>
    /// <param name="bytes">plugins.js の内容。</param>
    public static PluginsJsObservation Observe(byte[] bytes)
    {
        var sha = Hashing.Sha256Hex(bytes);
        var looksCorrupt = LooksLikeCorruption(bytes);
        var mentionsPlugin = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(GameConstants.PluginName)) >= 0;
        try
        {
            var (text, _) = Utf8Text.Decode(bytes, "plugins.js");
            return new PluginsJsObservation(sha, PluginsJsEditor.Parse(text).Contains(GameConstants.PluginName), null)
            {
                LooksCorrupt = looksCorrupt,
                MentionsPluginName = mentionsPlugin,
            };
        }
        catch (Exception ex) when (ex is PatchException or PluginsJsFormatException)
        {
            return new PluginsJsObservation(sha, null, ex.Message)
            {
                LooksCorrupt = looksCorrupt,
                MentionsPluginName = mentionsPlugin,
            };
        }
    }

    /// <summary>
    /// 内容が破損らしいか（長さ 0・NUL を含む・UTF-8 として不正）を判定する純粋関数（CA-07）。
    /// テキストの plugins.js には NUL は現れず、RPGツクールMV は UTF-8 で書き出すため、これらは破損の兆候とみなす。
    /// </summary>
    /// <param name="bytes">plugins.js の内容。</param>
    internal static bool LooksLikeCorruption(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty || bytes.Contains((byte)0) || !System.Text.Unicode.Utf8.IsValid(bytes);
}

/// <summary>plugins.js を元に戻す方法。</summary>
public enum PluginsJsRevertMethod
{
    /// <summary>何もしない（パッチのエントリが無い）。</summary>
    None,

    /// <summary>適用前の内容（バックアップ）で丸ごと置き換える。</summary>
    ReplaceWithOriginal,

    /// <summary>パッチのエントリだけをテキスト上で取り除く（他のプログラムによる変更は保つ）。</summary>
    RemoveEntry,

    /// <summary>自動では戻せない（解析できず、使えるバックアップも無い）。</summary>
    Unrecoverable,
}

/// <summary>plugins.js を元に戻す方法の判定結果。</summary>
/// <param name="Method">戻す方法。</param>
/// <param name="Reason">そう判断した理由（ログ・エラー表示用の日本語）。</param>
public sealed record PluginsJsRevertDecision(PluginsJsRevertMethod Method, string Reason);

/// <summary>
/// plugins.js を元に戻す方法を決める純粋関数。
/// 復元（<see cref="PatchRestorer"/>）と、適用失敗時の取り消し（<see cref="PatchApplier"/>）で同じ規則を使う。
/// 規則:
/// M. plugins.js が無い場合は、記録と一致するバックアップがあればそれで作り直す。無ければ自動では作り直せない（CA-05）。
/// 0. 現在の内容が記録された「適用前」と一致していれば何もしない。
/// 1. 現在の内容が「適用直後」と一致し、適用前の内容（バックアップ）が記録と一致すれば、それで丸ごと戻す（バイト単位で元通り）。
/// 2. 現在の内容を解析できない場合（CA-07 で「破損らしい」場合に限定）:
///    2a. 破損らしい（長さ 0・NUL を含む・UTF-8 として不正）なら、記録と一致するバックアップがあればそれで戻す。無ければ自動では戻せない。
///    2b. 破損ではない正しいテキスト（厳密な JSON でないだけ。Steam の更新や他の MOD が書いた可能性がある）は上書きしない。
///        プラグイン名の文字列を含まなければ何もしない（そのまま残す）。含めば、パッチの登録が残っている可能性があるため自動では戻せない（案内付き）。
/// 3. それ以外でエントリが残っていれば、エントリだけを取り除く（Steam の更新や他の MOD による変更を消さない）。
/// 4. エントリが無ければ何もしない（適用後に別の内容へ置き換わっていても、そのまま残す）。
/// </summary>
public static class PluginsJsRevertPlanner
{
    /// <summary>
    /// 元に戻す方法を決める。
    /// </summary>
    /// <param name="current">現在の plugins.js の様子（ファイルが無い場合は null。ロックや権限で読めない場合は呼び出し側で失敗させ、null にしないこと）。</param>
    /// <param name="originalSha256">記録された適用前の SHA-256（記録が無ければ null）。</param>
    /// <param name="patchedSha256">記録された適用直後の SHA-256（記録が無ければ null）。</param>
    /// <param name="availableOriginalSha256">手元にある適用前の内容（バックアップ）の SHA-256（無ければ null）。</param>
    public static PluginsJsRevertDecision Decide(
        PluginsJsObservation? current,
        string? originalSha256,
        string? patchedSha256,
        string? availableOriginalSha256)
    {
        var backupUsable = originalSha256 is not null
            && string.Equals(availableOriginalSha256, originalSha256, StringComparison.Ordinal);

        // M. plugins.js が無い（ウイルス対策ソフトの隔離・誤削除など）: 記録と一致するバックアップがあれば作り直す（CA-05）
        if (current is null)
        {
            return backupUsable
                ? new PluginsJsRevertDecision(PluginsJsRevertMethod.ReplaceWithOriginal, "plugins.js が無いため、記録と一致するバックアップから作り直しています…")
                : new PluginsJsRevertDecision(
                    PluginsJsRevertMethod.Unrecoverable,
                    $"plugins.js が無く、記録と一致するバックアップも無いため、自動では作り直せません。{PatchMessages.SteamVerifyGuidance}");
        }

        // 既に適用前の内容と一致している（置き換え前に失敗した、または Steam の整合性チェックで戻された）
        if (originalSha256 is not null && string.Equals(current.Sha256, originalSha256, StringComparison.Ordinal))
        {
            return new PluginsJsRevertDecision(PluginsJsRevertMethod.None, "plugins.js は適用前の内容のままです。");
        }

        if (patchedSha256 is not null && string.Equals(current.Sha256, patchedSha256, StringComparison.Ordinal))
        {
            if (backupUsable)
            {
                return new PluginsJsRevertDecision(PluginsJsRevertMethod.ReplaceWithOriginal, "plugins.js をバックアップから戻しています…");
            }

            return new PluginsJsRevertDecision(
                PluginsJsRevertMethod.RemoveEntry,
                availableOriginalSha256 is null
                    ? "バックアップが見つからないため、エントリの除去で戻します。"
                    : "バックアップの内容が記録と一致しないため、エントリの除去で戻します。");
        }

        if (current.HasEntry is not { } hasEntry)
        {
            // 2b. 破損ではない正しいテキスト（厳密な JSON でないだけ）は、古いバックアップで上書きしない（CA-07）
            if (!current.LooksCorrupt)
            {
                return current.MentionsPluginName
                    ? new PluginsJsRevertDecision(
                        PluginsJsRevertMethod.Unrecoverable,
                        $"plugins.js を解析できず（{current.ParseError}）、パッチの登録（{GameConstants.PluginName}）が含まれている可能性があるため、自動では取り除けません。"
                        + $"他のプログラムが書き換えた内容を消さないよう、何も変更していません。plugins.js から {GameConstants.PluginName} の登録を取り除くか、{PatchMessages.SteamVerifyGuidance}")
                    : new PluginsJsRevertDecision(
                        PluginsJsRevertMethod.None,
                        $"plugins.js は解析できませんが（{current.ParseError}）、破損ではなく（他のプログラムが書き換えた可能性があります）、パッチの登録も含まれていないため、そのままにします。");
            }

            // 2a. 破損らしい内容は、記録と一致するバックアップがあればそれで戻す
            return backupUsable
                ? new PluginsJsRevertDecision(
                    PluginsJsRevertMethod.ReplaceWithOriginal,
                    $"plugins.js を解析できないため（{current.ParseError}）、バックアップから適用前の内容に戻します。")
                : new PluginsJsRevertDecision(
                    PluginsJsRevertMethod.Unrecoverable,
                    $"plugins.js を解析できず（{current.ParseError}）、使えるバックアップも無いため自動では戻せません。{PatchMessages.SteamVerifyGuidance}");
        }

        return hasEntry
            ? new PluginsJsRevertDecision(PluginsJsRevertMethod.RemoveEntry, "plugins.js からプラグインの登録を取り除いています…")
            : new PluginsJsRevertDecision(
                PluginsJsRevertMethod.None,
                patchedSha256 is null
                    ? "plugins.js にパッチのエントリはありません。"
                    : "plugins.js は適用後に別の内容へ置き換わっており、パッチのエントリも無いため、そのままにします。");
    }
}

/// <summary>
/// <see cref="PluginsJsRevertPlanner"/> の判定に従って plugins.js を書き換える手続き（副作用はここに閉じ込める）。
/// </summary>
internal static class PluginsJsReverter
{
    /// <summary>
    /// plugins.js を読む。ファイル（またはフォルダ）が見つからない場合だけ null を返す（CA-05）。
    /// ロック・アクセス拒否などで読めない場合は「無い」とみなさず、例外をそのまま伝える（読めないだけのファイルをバックアップで上書きしないため）。
    /// </summary>
    /// <param name="fileSystem">ファイル操作。</param>
    /// <param name="pluginsFullPath">plugins.js のフルパス。</param>
    public static byte[]? ReadIfExists(IPatchFileSystem fileSystem, string pluginsFullPath)
    {
        try
        {
            return fileSystem.ReadAllBytes(pluginsFullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判定に従って plugins.js を戻す。
    /// </summary>
    /// <param name="fileSystem">ファイル操作。</param>
    /// <param name="pluginsFullPath">plugins.js のフルパス。</param>
    /// <param name="decision">戻す方法。</param>
    /// <param name="currentBytes">判定に使った現在の内容（ファイルが無かった場合は null）。</param>
    /// <param name="originalBytes">適用前の内容（バックアップ。無ければ null）。</param>
    /// <param name="log">進行状況の通知先。</param>
    /// <exception cref="PatchException">自動では戻せない場合、またはエントリの除去に失敗した場合。</exception>
    public static void Execute(
        IPatchFileSystem fileSystem,
        string pluginsFullPath,
        PluginsJsRevertDecision decision,
        byte[]? currentBytes,
        byte[]? originalBytes,
        Action<string> log)
    {
        switch (decision.Method)
        {
            case PluginsJsRevertMethod.None:
                log(decision.Reason);
                return;

            case PluginsJsRevertMethod.ReplaceWithOriginal:
                log(decision.Reason);
                fileSystem.ReplaceFile(
                    pluginsFullPath,
                    originalBytes ?? throw new InvalidOperationException("バックアップの内容が無いのに、バックアップから戻す判定になりました。"));
                return;

            case PluginsJsRevertMethod.RemoveEntry:
                log(decision.Reason);
                var (text, hasBom) = Utf8Text.Decode(
                    currentBytes ?? throw new InvalidOperationException("plugins.js が無いのに、エントリの除去で戻す判定になりました。"),
                    "plugins.js");
                var (updated, changed) = PluginsJsFormat.Guard(() => PluginsJsEditor.RemoveEntry(text, GameConstants.PluginName));
                if (changed)
                {
                    fileSystem.ReplaceFile(pluginsFullPath, Utf8Text.Encode(updated, hasBom));
                }

                return;

            case PluginsJsRevertMethod.Unrecoverable:
                throw new PatchException(decision.Reason);

            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision.Method, "未知の戻し方です。");
        }
    }
}

/// <summary>
/// plugins.js の形式エラー（<see cref="PluginsJsFormatException"/>）を、対処方法の案内つきの <see cref="PatchException"/> に包む補助。
/// </summary>
internal static class PluginsJsFormat
{
    /// <summary>対処方法の案内文（整合性の確認で他の MOD などの編集が消えることも伝える。CA-08）。</summary>
    public const string Guidance = PatchMessages.SteamVerifyGuidance;

    /// <summary>処理を実行し、plugins.js の形式エラーを案内つきの <see cref="PatchException"/> に変換する。</summary>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="operation">plugins.js を解析・編集する処理。</param>
    public static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (PluginsJsFormatException ex)
        {
            throw new PatchException($"{ex.Message}{Environment.NewLine}{Guidance}", ex);
        }
    }
}
