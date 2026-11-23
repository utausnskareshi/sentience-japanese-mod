using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace SentienceJP.Core.Steam;

/// <summary>
/// レジストリから得た Steam のインストール先候補と、取得時に起きた問題の記録。
/// </summary>
/// <param name="Roots">Steam 本体フォルダの候補（優先度順）。</param>
/// <param name="Diagnostics">読み取れなかったキーなどの診断メッセージ（ログ用）。</param>
public sealed record SteamRootCandidates(IReadOnlyList<string> Roots, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Steam のインストール先をレジストリから取得する抽象。テストでは偽の実装に差し替える。
/// </summary>
public interface ISteamRegistry
{
    /// <summary>Steam 本体フォルダの候補を優先度順に返す。</summary>
    SteamRootCandidates GetSteamRootCandidates();
}

/// <summary>
/// Windows のレジストリから Steam のインストール先を読む実装。
/// 優先度: HKCU\Software\Valve\Steam\SteamPath → HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath → HKLM\SOFTWARE\Valve\Steam\InstallPath
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSteamRegistry : ISteamRegistry
{
    /// <summary>読み取るレジストリ値の定義（ハイブ・キー・値の名前）。</summary>
    private static readonly (RegistryKey Hive, string HiveName, string SubKey, string ValueName)[] Sources =
    [
        (Registry.CurrentUser, "HKCU", @"Software\Valve\Steam", "SteamPath"),
        (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        (Registry.LocalMachine, "HKLM", @"SOFTWARE\Valve\Steam", "InstallPath"),
    ];

    /// <inheritdoc />
    public SteamRootCandidates GetSteamRootCandidates()
    {
        var roots = new List<string>();
        var diagnostics = new List<string>();

        foreach (var (hive, hiveName, subKey, valueName) in Sources)
        {
            var location = $@"{hiveName}\{subKey}\{valueName}";
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    roots.Add(value);
                }
                else
                {
                    diagnostics.Add($"レジストリ値がありません: {location}");
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // 1 つのキーが読めなくても他の候補で検出できる可能性があるため、診断に記録して続行する
                diagnostics.Add($"レジストリを読み取れませんでした: {location}（{ex.Message}）");
            }
        }

        return new SteamRootCandidates(roots, diagnostics);
    }
}
