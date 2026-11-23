namespace SentienceJP.Core;

/// <summary>
/// ゲームルートからの相対パス（"/" 区切り）を扱う純粋関数群。
/// ペイロードの manifest に書かれたパスでゲームフォルダの外へ書き込む事故（パストラバーサル）を防ぐため、
/// 相対パスはここで必ず検証してからフルパスへ変換する。
/// </summary>
public static class GamePaths
{
    /// <summary>
    /// Windows の予約デバイス名。拡張子が付いていても（例: "nul.txt"）デバイスとして扱われるため、ファイル名に使わせない。
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// 相対パスが安全な形式か検証する。問題があれば <see cref="PatchException"/> を投げる。
    /// 許可する形式: 空でない、絶対パスでない、"/" 区切り、"." や ".." を含まない、ドライブ指定やバックスラッシュを含まない、
    /// Windows が別のファイルとして解釈する名前（末尾の "." や空白、予約デバイス名、8.3 形式の短い名前）を含まない。
    /// </summary>
    /// <param name="relativePath">ゲームルートからの相対パス。</param>
    public static void ValidateRelativePath(string relativePath)
    {
        var problem = DescribeProblem(relativePath);
        if (problem is not null)
        {
            throw new PatchException($"不正な相対パスです（{problem}）: \"{relativePath}\"");
        }
    }

    /// <summary>
    /// 相対パスの問題点を説明する文字列を返す。問題が無ければ null。
    /// 例外を投げない版（検証結果を値として扱いたい場面用）。
    /// </summary>
    /// <param name="relativePath">ゲームルートからの相対パス。</param>
    public static string? DescribeProblem(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "空のパス";
        }

        if (relativePath.Contains('\\', StringComparison.Ordinal))
        {
            return "区切り文字は \"/\" のみ使用可能";
        }

        if (relativePath.Contains(':', StringComparison.Ordinal))
        {
            return "ドライブ指定やストリーム指定は使用不可";
        }

        if (relativePath.StartsWith('/'))
        {
            return "絶対パスは使用不可";
        }

        var segments = relativePath.Split('/');
        if (segments.Any(s => s.Length == 0))
        {
            return "空の区切りを含む";
        }

        if (segments.Any(s => s is "." or ".."))
        {
            return "\".\" や \"..\" は使用不可";
        }

        if (segments.Any(s => s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return "ファイル名に使えない文字を含む";
        }

        // Windows は末尾の "." と空白を取り除いて解釈するため、"plugins.js." は "plugins.js" の別名になる
        if (segments.Any(s => s.EndsWith('.') || s.EndsWith(' ')))
        {
            return "末尾が \".\" や空白の名前は使用不可（Windows では別のファイルの別名になる）";
        }

        if (segments.Any(IsReservedDeviceName))
        {
            return "予約されたデバイス名（CON・NUL・COM1 など）は使用不可";
        }

        // "PLUGIN~1.JS" のような 8.3 形式の短い名前は、既存の長い名前のファイルを指す可能性がある
        if (segments.Any(LooksLikeShortName))
        {
            return "8.3 形式の短い名前（\"~\" と数字を含む名前）は使用不可";
        }

        return null;
    }

    /// <summary>
    /// パッチが追加してよいファイルか（許可リスト方式）を判定する。
    /// 許可するのは <see cref="GameConstants.PluginFileRelativePath"/> と、
    /// <see cref="GameConstants.PatchOwnedDirectories"/> の配下だけ（大文字小文字は区別しない）。
    /// 既存のゲームファイルやバックアップ領域を、manifest や状態ファイルの記述で消したり上書きしたりしないための制限。
    /// </summary>
    /// <param name="relativePath">ゲームルートからの相対パス。</param>
    public static bool IsPatchFile(string relativePath) =>
        DescribeProblem(relativePath) is null
        && (string.Equals(relativePath, GameConstants.PluginFileRelativePath, StringComparison.OrdinalIgnoreCase)
            || GameConstants.PatchOwnedDirectories.Any(directory =>
                relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// パッチが作成し、復元時に「空なら」削除してよいフォルダか（許可リスト方式）を判定する。
    /// 許可するのは、プラグインファイルの親フォルダ（www・www/js・www/js/plugins。既存のゲームなら必ず存在するため実際には作られない）と、
    /// <see cref="GameConstants.PatchOwnedDirectories"/> 自身およびその配下。
    /// </summary>
    /// <param name="relativePath">ゲームルートからの相対パス。</param>
    public static bool IsPatchDirectory(string relativePath) =>
        DescribeProblem(relativePath) is null
        && (AncestorDirectories(GameConstants.PluginFileRelativePath).Contains(relativePath, StringComparer.OrdinalIgnoreCase)
            || GameConstants.PatchOwnedDirectories.Any(directory =>
                string.Equals(relativePath, directory, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// パスの 1 区切り（ファイル名・フォルダ名）が予約デバイス名かを判定する。
    /// 拡張子の前の部分（末尾の空白は無視）で判定する（"nul.txt"・"CON .json" も予約名として扱われる）。
    /// </summary>
    private static bool IsReservedDeviceName(string segment)
    {
        var dot = segment.IndexOf('.', StringComparison.Ordinal);
        var baseName = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return ReservedDeviceNames.Contains(baseName);
    }

    /// <summary>"~" の直後に数字が続く（8.3 形式の短い名前の特徴）かを判定する。</summary>
    private static bool LooksLikeShortName(string segment) =>
        Enumerable.Range(0, Math.Max(0, segment.Length - 1))
            .Any(index => segment[index] == '~' && char.IsAsciiDigit(segment[index + 1]));

    /// <summary>
    /// 相対パスを検証し、ゲームフォルダ配下のフルパスへ変換する。
    /// 変換結果がゲームフォルダの外を指す場合は <see cref="PatchException"/> を投げる。
    /// </summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    /// <param name="relativePath">ゲームルートからの相対パス（空文字はゲームルート自身）。</param>
    public static string ToFullPath(string gameDirectory, string relativePath)
    {
        var root = Path.GetFullPath(gameDirectory);
        if (relativePath.Length == 0)
        {
            return root;
        }

        ValidateRelativePath(relativePath);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderDirectory(combined, root))
        {
            throw new PatchException($"ゲームフォルダの外を指すパスは扱えません: \"{relativePath}\"");
        }

        return combined;
    }

    /// <summary>
    /// <paramref name="path"/> が <paramref name="directory"/> の配下（同一を含まない）にあるかを判定する。
    /// Windows のパスは大文字小文字を区別しないため、序数の大文字小文字無視で比較する。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <param name="directory">基準のフォルダ。</param>
    public static bool IsUnderDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(WithTrailingSeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// フォルダのフルパスの末尾に区切り文字を 1 つだけ付けた形にする純粋関数（CA-14）。
    /// 既に区切りで終わっていれば（ドライブ直下 "C:\" や、末尾に区切りのあるパス）そのまま返し、区切りを二重にしない
    /// （以前はルートの区切りを取り除けずに "C:\\" となり、ドライブ直下ではすべてのパスを「外」と判定していた）。
    /// </summary>
    /// <param name="fullDirectory">フォルダのフルパス（<see cref="Path.GetFullPath(string)"/> の結果）。</param>
    internal static string WithTrailingSeparator(string fullDirectory) =>
        Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;

    /// <summary>
    /// 相対パスの親フォルダ（"/" 区切り）を列挙する。浅い順に返し、ルート（空文字）は含めない。
    /// 例: "www/sentience_jp/fonts/a.ttf" → ["www", "www/sentience_jp", "www/sentience_jp/fonts"]
    /// </summary>
    /// <param name="relativePath">ゲームルートからの相対パス。</param>
    public static IReadOnlyList<string> AncestorDirectories(string relativePath)
    {
        var segments = relativePath.Split('/');
        return Enumerable.Range(1, Math.Max(0, segments.Length - 1))
            .Select(count => string.Join('/', segments.Take(count)))
            .ToList();
    }

    /// <summary>
    /// 相対パスの深さ（区切りの数）を返す。フォルダを深い順に処理する際の並べ替えに使う。
    /// </summary>
    /// <param name="relativePath">ゲームルートからの相対パス。</param>
    public static int Depth(string relativePath) => relativePath.Count(c => c == '/');
}
