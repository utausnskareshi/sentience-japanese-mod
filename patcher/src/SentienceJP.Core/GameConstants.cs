namespace SentienceJP.Core;

/// <summary>
/// 対象ゲームとパッチに関する固定値。
/// TypeScript 側（ペイロード生成・ゲーム内プラグイン）と共有する「契約」の値なので、
/// 変更する場合は必ず両方を揃えること。
/// </summary>
public static class GameConstants
{
    /// <summary>Sentience: The Android's Tale の Steam AppID。</summary>
    public const string SteamAppId = "635850";

    /// <summary>メッセージ表示用のゲーム名。</summary>
    public const string GameTitle = "Sentience: The Android's Tale";

    /// <summary>plugins.js に登録するプラグイン名（ゲーム内プラグインのファイル名と一致させる）。</summary>
    public const string PluginName = "SentienceJP";

    /// <summary>ゲームルートからの plugins.js の相対パス（"/" 区切り）。</summary>
    public const string PluginsJsRelativePath = "www/js/plugins.js";

    /// <summary>バックアップと状態ファイルを置くフォルダ名（ゲームルート直下に作る）。</summary>
    public const string BackupDirectoryName = "sentience_jp_backup";

    /// <summary>バックアップフォルダ内で、元ファイルのコピーを置くサブフォルダ名。</summary>
    public const string BackupOriginalDirectoryName = "original";

    /// <summary>バックアップフォルダ内の状態ファイル名。</summary>
    public const string StateFileName = "state.json";

    /// <summary>状態ファイルの形式バージョン。</summary>
    public const int StateSchemaVersion = 1;

    /// <summary>対応しているペイロード manifest.json の形式バージョン。</summary>
    public const int ManifestSchemaVersion = 1;

    /// <summary>ゲームの実行ファイルのプロセス名（拡張子なし）。起動中チェックに使う。</summary>
    public const string GameProcessName = "nw";

    /// <summary>
    /// ゲーム内プラグインのファイル（ゲームルートからの相対パス、"/" 区切り）。
    /// パッチが www/js/plugins 配下に追加してよい唯一のファイル（許可リストの一部）。
    /// </summary>
    public const string PluginFileRelativePath = "www/js/plugins/" + PluginName + ".js";

    /// <summary>
    /// パッチ専用のデータフォルダ（訳文データ等。ゲームルートからの相対パス、"/" 区切り）。
    /// パッチはこのフォルダ配下にだけファイルとフォルダを追加する（許可リストの一部）。
    /// </summary>
    public const string PatchDataDirectoryRelativePath = "www/sentience_jp";

    /// <summary>
    /// ファイルを安全に置き換えるときに使う一時ファイルの接尾辞（対象と同じフォルダに「対象のファイル名 + 接尾辞」で作る）。
    /// 中断で残った一時ファイルを状態判定と復元で見つけて片付けるため、名前の規則はここで一元管理する。
    /// </summary>
    public const string TemporaryFileSuffix = ".sentience_jp.tmp";

    /// <summary>
    /// パッチが一時ファイル経由で置き換える、ゲーム側の既存ファイル（ゲームルートからの相対パス）。
    /// 置き換えの途中で中断すると「&lt;パス&gt;<see cref="TemporaryFileSuffix"/>」が残るため、状態判定と復元で確認する。
    /// （state.json の一時ファイルはバックアップフォルダ内にあり、フォルダごと削除されるため対象外）
    /// </summary>
    public static readonly IReadOnlyList<string> FilesReplacedViaTemporary =
    [
        PluginsJsRelativePath,
    ];

    /// <summary>
    /// 有効なゲームフォルダと判定するために必要なファイル（ゲームルートからの相対パス、"/" 区切り）。
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredGameFiles =
    [
        "nw.exe",
        "www/data/System.json",
        PluginsJsRelativePath,
    ];

    /// <summary>
    /// パッチが独自に作るフォルダ（ゲームルートからの相対パス）。
    /// 状態ファイルが失われていても、復元時にこのフォルダ配下の空フォルダは削除してよい。
    /// また、パッチが追加してよいファイルはこのフォルダ配下と <see cref="PluginFileRelativePath"/> だけに限る（許可リスト）。
    /// </summary>
    public static readonly IReadOnlyList<string> PatchOwnedDirectories =
    [
        PatchDataDirectoryRelativePath,
    ];

    /// <summary>
    /// 書き込み権限を事前確認するフォルダ（ゲームルートからの相対パス、空文字はゲームルート）。
    /// バックアップ作成（ルート）、追加ファイル（www 配下）、plugins.js の置換（www/js）に必要。
    /// </summary>
    public static readonly IReadOnlyList<string> DirectoriesRequiringWriteAccess =
    [
        "",
        "www",
        "www/js",
        "www/js/plugins",
    ];
}
