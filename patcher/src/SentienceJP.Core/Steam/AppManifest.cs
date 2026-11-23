using System.Globalization;

namespace SentienceJP.Core.Steam;

/// <summary>
/// appmanifest_&lt;appid&gt;.acf から読み取ったアプリの情報。
/// </summary>
/// <param name="AppId">AppID。</param>
/// <param name="Name">アプリ名（無い場合は null）。</param>
/// <param name="InstallDir">steamapps/common 配下のインストールフォルダ名。</param>
/// <param name="BuildId">ビルドID（無い場合は null）。</param>
/// <param name="StateFlags">Steam の状態フラグ（無い・数値でない場合は null）。4 は「インストール完了」。</param>
/// <param name="LastUpdated">最終更新日時（UNIX 時間の秒。無い・数値でない場合は null）。複数のライブラリに appmanifest がある場合の選択に使う。</param>
public sealed record AppManifest(string AppId, string? Name, string InstallDir, string? BuildId, int? StateFlags, long? LastUpdated = null)
{
    /// <summary>Steam 上で「インストール完了」状態（StateFlags == 4）かどうか。</summary>
    public bool IsFullyInstalled => StateFlags == 4;

    /// <summary>
    /// ACF の内容を解析する。
    /// </summary>
    /// <param name="acfText">appmanifest_*.acf の内容。</param>
    /// <exception cref="KeyValuesParseException">構文が不正、または必須項目（appid、installdir）が無い場合。</exception>
    public static AppManifest Parse(string acfText)
    {
        var root = KeyValuesParser.Parse(acfText);
        var state = root.GetObject("AppState")
            ?? throw new KeyValuesParseException("appmanifest に \"AppState\" ブロックがありません。");

        var appId = state.GetString("appid");
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new KeyValuesParseException("appmanifest に \"appid\" がありません。");
        }

        var installDir = state.GetString("installdir");
        if (string.IsNullOrWhiteSpace(installDir))
        {
            throw new KeyValuesParseException("appmanifest に \"installdir\" がありません。");
        }

        var stateFlags = int.TryParse(state.GetString("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)
            ? flags
            : (int?)null;
        var lastUpdated = long.TryParse(state.GetString("LastUpdated"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var updated)
            ? updated
            : (long?)null;

        return new AppManifest(appId, state.GetString("name"), installDir, state.GetString("buildid"), stateFlags, lastUpdated);
    }
}
