namespace SentienceJP.Core.Tests.TestSupport;

/// <summary>
/// 実際にインストールされたゲームのファイルを「読み取り専用で」使うテスト。
/// ゲームが見つからない環境（CI 等）では自動的にスキップする。
/// </summary>
public sealed class RealGameFactAttribute : FactAttribute
{
    /// <summary>このPCでの実ゲームの plugins.js の場所。</summary>
    public const string RealPluginsJsPath = @"D:\SteamLibrary\steamapps\common\Sentience The Android's Tale\www\js\plugins.js";

    /// <summary>実ゲームが無ければスキップ理由を設定する。</summary>
    public RealGameFactAttribute()
    {
        if (!File.Exists(RealPluginsJsPath))
        {
            Skip = $"実ゲームの plugins.js が見つからないためスキップします: {RealPluginsJsPath}";
        }
    }
}
