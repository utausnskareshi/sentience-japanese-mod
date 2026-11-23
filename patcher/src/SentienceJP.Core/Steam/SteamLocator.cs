namespace SentienceJP.Core.Steam;

/// <summary>
/// 検出したゲームのインストール情報。
/// </summary>
/// <param name="GameDirectory">ゲームのルートフォルダ（nw.exe があるフォルダ）。</param>
/// <param name="LibraryDirectory">ゲームが入っている Steam ライブラリのフォルダ。</param>
/// <param name="ManifestPath">appmanifest_*.acf のパス。</param>
/// <param name="Manifest">appmanifest の内容。</param>
public sealed record GameInstallation(string GameDirectory, string LibraryDirectory, string ManifestPath, AppManifest Manifest);

/// <summary>
/// ゲーム検出の結果。見つからなかった場合も、理由の手掛かりを診断メッセージとして返す。
/// </summary>
/// <param name="Installation">見つかったインストール情報（見つからなければ null）。</param>
/// <param name="Diagnostics">探索中の診断メッセージ（ログ用）。</param>
public sealed record GameLocateResult(GameInstallation? Installation, IReadOnlyList<string> Diagnostics);

/// <summary>
/// Steam のライブラリを走査して、対象ゲームのインストール先を探す。
/// 判定は「appmanifest_&lt;appid&gt;.acf が実在し、installdir のフォルダに必須ファイルが揃っている」こと。
/// 複数のライブラリに該当する appmanifest がある場合（移動前の古い appmanifest が残っている等）は、
/// インストール完了状態（StateFlags=4）のもの、次に最終更新日時（LastUpdated）が新しいものを選ぶ。
/// </summary>
public static class SteamLocator
{
    /// <summary>
    /// Steam 本体フォルダの候補から、対象 AppID のゲームを探す。
    /// </summary>
    /// <param name="steamRoots">Steam 本体フォルダの候補（優先度順）。</param>
    /// <param name="appId">探す AppID。</param>
    public static GameLocateResult Locate(IReadOnlyList<string> steamRoots, string appId)
    {
        var diagnostics = new List<string>();
        var libraries = EnumerateLibraries(steamRoots, diagnostics);
        diagnostics.Add($"探索するライブラリ（{libraries.Count} 件）: {(libraries.Count == 0 ? "なし" : string.Join(", ", libraries))}");

        var manifestCount = 0;
        var candidates = new List<GameInstallation>();
        foreach (var library in libraries)
        {
            var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            manifestCount++;
            var manifest = TryReadManifest(manifestPath, diagnostics);
            if (manifest is null)
            {
                continue;
            }

            // installdir は steamapps\common 配下の単一のフォルダ名でなければならない（CA-12・CA-13）。
            // 区切り文字・".."・ドライブ指定・NUL などを含む壊れた・改変された appmanifest は、例外を漏らさずに診断へ記録してこのライブラリを飛ばす
            // （1 つの壊れた appmanifest のために、他のライブラリの正常なインストールを見つけられなくなったり、起動処理ごと終了したりしないため）
            var gameDirectory = TryResolveGameDirectory(library, manifest.InstallDir, manifestPath, diagnostics);
            if (gameDirectory is null)
            {
                continue;
            }

            var missing = GameValidator.FindMissingFiles(gameDirectory);
            if (missing.Count == 0)
            {
                diagnostics.Add($"候補: {gameDirectory}（StateFlags={manifest.StateFlags?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "不明"}、"
                    + $"LastUpdated={manifest.LastUpdated?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "不明"}、buildid={manifest.BuildId ?? "不明"}）");
                candidates.Add(new GameInstallation(gameDirectory, library, manifestPath, manifest));
                continue;
            }

            diagnostics.Add($"appmanifest はありますが、ゲームフォルダに必要なファイルがありません: {gameDirectory}（不足: {string.Join(", ", missing)}）");
        }

        var chosen = ChooseBest(candidates);
        if (chosen is not null)
        {
            if (candidates.Count > 1)
            {
                diagnostics.Add($"複数の候補から選びました: {chosen.GameDirectory}（インストール完了状態のもの、次に最終更新日時の新しいものを優先）");
            }

            return new GameLocateResult(chosen, diagnostics);
        }

        diagnostics.Add(manifestCount == 0
            ? $"AppID {appId} の appmanifest が見つかりませんでした（探索したライブラリ: {libraries.Count} 件）。"
            : $"AppID {appId} の appmanifest は {manifestCount} 件見つかりましたが、使えるゲームフォルダがありませんでした（理由は上の記録を参照）。");
        return new GameLocateResult(null, diagnostics);
    }

    /// <summary>
    /// 候補の中から使うインストール先を選ぶ純粋関数。
    /// 優先順: インストール完了状態（StateFlags=4）→ 最終更新日時が新しい → ライブラリの列挙順（先に見つかったもの）。
    /// </summary>
    /// <param name="candidates">必須ファイルが揃っているインストール先（ライブラリの列挙順）。</param>
    internal static GameInstallation? ChooseBest(IReadOnlyList<GameInstallation> candidates) =>
        candidates
            .Select((candidate, index) => (Candidate: candidate, Index: index))
            .OrderByDescending(pair => pair.Candidate.Manifest.IsFullyInstalled)
            .ThenByDescending(pair => pair.Candidate.Manifest.LastUpdated ?? long.MinValue)
            .ThenBy(pair => pair.Index)
            .Select(pair => pair.Candidate)
            .FirstOrDefault();

    /// <summary>
    /// Steam 本体フォルダの候補から、重複を除いたライブラリフォルダの一覧を作る。
    /// libraryfolders.vdf に書かれた表記（大文字小文字が正しい）を優先し、Steam 本体フォルダ自身も必ず含める。
    /// </summary>
    private static List<string> EnumerateLibraries(IReadOnlyList<string> steamRoots, List<string> diagnostics)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLibrary(string path)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostics.Add($"ライブラリのパスとして解釈できません: \"{path}\"（{ex.Message}）");
                return;
            }

            if (seen.Add(Path.TrimEndingDirectorySeparator(normalized)))
            {
                result.Add(normalized);
            }
        }

        foreach (var root in steamRoots)
        {
            string rootFullPath;
            try
            {
                rootFullPath = Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostics.Add($"Steam のパスとして解釈できません: \"{root}\"（{ex.Message}）");
                continue;
            }

            if (!Directory.Exists(rootFullPath))
            {
                diagnostics.Add($"Steam のフォルダが存在しません: {rootFullPath}");
                continue;
            }

            var vdfPath = Path.Combine(rootFullPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    foreach (var libraryPath in SteamLibraryFolders.ParseLibraryPaths(File.ReadAllText(vdfPath)))
                    {
                        AddLibrary(libraryPath);
                    }
                }
                catch (Exception ex) when (ex is KeyValuesParseException or IOException or UnauthorizedAccessException)
                {
                    // vdf が読めなくても Steam 本体フォルダ自身は探索できるため、診断に記録して続行する
                    diagnostics.Add($"libraryfolders.vdf を読み取れませんでした: {vdfPath}（{ex.Message}）");
                }
            }
            else
            {
                diagnostics.Add($"libraryfolders.vdf がありません: {vdfPath}");
            }

            AddLibrary(rootFullPath);
        }

        return result;
    }

    /// <summary>
    /// appmanifest の installdir が、steamapps\common 配下の単一のフォルダ名として妥当かを調べる純粋関数（CA-12・CA-13）。
    /// 問題があればその説明を、無ければ null を返す。
    /// 拒否するもの: 空・"." と ".."・フォルダ名に使えない文字（"\"・"/" の区切り文字、":" のドライブ指定、NUL などの制御文字、"*?&lt;&gt;|\""）・
    /// 末尾の "." や空白（Windows では別の名前の別名になる）。"Sentience The Android's Tale" のような空白やアポストロフィは許可する。
    /// </summary>
    /// <param name="installDir">appmanifest の installdir。</param>
    internal static string? DescribeInstallDirProblem(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return "空です";
        }

        if (installDir is "." or "..")
        {
            return "\".\" や \"..\" は使えません";
        }

        if (installDir.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "フォルダ名に使えない文字（区切り文字・ドライブ指定・制御文字など）を含みます";
        }

        if (installDir.EndsWith('.') || installDir.EndsWith(' '))
        {
            return "末尾が \".\" や空白の名前は使えません";
        }

        return null;
    }

    /// <summary>
    /// ライブラリと installdir からゲームフォルダのフルパスを求める。installdir が不正、またはパスが steamapps\common の外を指す場合は、
    /// 診断に記録して null を返す（例外を呼び出し元へ漏らさない）。
    /// </summary>
    private static string? TryResolveGameDirectory(string library, string installDir, string manifestPath, List<string> diagnostics)
    {
        if (DescribeInstallDirProblem(installDir) is { } problem)
        {
            diagnostics.Add($"appmanifest の installdir が不正なため、このライブラリを飛ばします: {manifestPath}（{problem}: \"{Printable(installDir)}\"）");
            return null;
        }

        var commonDirectory = Path.Combine(library, "steamapps", "common");
        string gameDirectory;
        try
        {
            gameDirectory = Path.GetFullPath(Path.Combine(commonDirectory, installDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add($"appmanifest の installdir をパスとして解釈できないため、このライブラリを飛ばします: {manifestPath}（\"{Printable(installDir)}\"、{ex.Message}）");
            return null;
        }

        // 念のため、結果が library\steamapps\common の配下であることも確かめる（単一のフォルダ名の検証をすり抜けた場合の多層防御）
        if (!GamePaths.IsUnderDirectory(gameDirectory, commonDirectory))
        {
            diagnostics.Add($"appmanifest の installdir が steamapps\\common の外を指しているため、このライブラリを飛ばします: {manifestPath}（{gameDirectory}）");
            return null;
        }

        return gameDirectory;
    }

    /// <summary>診断に書くため、制御文字（NUL など）を "\u0000" の形の見える表記に置き換える純粋関数。</summary>
    private static string Printable(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()));

    /// <summary>appmanifest を読み取る。失敗した場合は診断に記録して null を返す。</summary>
    private static AppManifest? TryReadManifest(string manifestPath, List<string> diagnostics)
    {
        try
        {
            return AppManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception ex) when (ex is KeyValuesParseException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"appmanifest を読み取れませんでした: {manifestPath}（{ex.Message}）");
            return null;
        }
    }
}
