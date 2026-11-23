using System.Reflection;

namespace SentienceJP.Core.Payload;

/// <summary>
/// ペイロード（manifest.json と追加ファイル群）の取得元の抽象。
/// 配布用 exe では埋め込みリソース、開発・テストではフォルダから読む。
/// </summary>
public interface IPayloadSource
{
    /// <summary>ログやエラー表示用の取得元の説明。</summary>
    string Description { get; }

    /// <summary>manifest.json の内容を読む。</summary>
    byte[] ReadManifest();

    /// <summary>ゲームに追加するファイルの内容を読む。</summary>
    /// <param name="relativePath">ゲームルートからの相対パス（"/" 区切り）。</param>
    byte[] ReadGameFile(string relativePath);
}

/// <summary>
/// フォルダ構成のペイロード（dist/payload）を読む実装。
/// 構成: &lt;root&gt;/manifest.json と &lt;root&gt;/game/&lt;相対パス&gt;。
/// </summary>
public sealed class DirectoryPayloadSource : IPayloadSource
{
    private readonly string _root;

    /// <summary>ペイロードのルートフォルダを指定して作る。</summary>
    /// <param name="rootDirectory">manifest.json があるフォルダ。</param>
    public DirectoryPayloadSource(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
    }

    /// <inheritdoc />
    public string Description => $"フォルダ: {_root}";

    /// <inheritdoc />
    public byte[] ReadManifest() => ReadFile(Path.Combine(_root, "manifest.json"), "manifest.json");

    /// <inheritdoc />
    public byte[] ReadGameFile(string relativePath)
    {
        var gameRoot = Path.Combine(_root, "game");
        return ReadFile(GamePaths.ToFullPath(gameRoot, relativePath), relativePath);
    }

    /// <summary>ファイルを読む。見つからない場合は分かりやすい例外にする。</summary>
    private static byte[] ReadFile(string fullPath, string displayName)
    {
        try
        {
            return File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PatchException($"ペイロードに {displayName} がありません（{fullPath}）。", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PatchException($"ペイロードの {displayName} を読み取れません（{fullPath}）: {ErrorDescriber.Describe(ex)}", ex);
        }
    }
}

/// <summary>
/// exe に埋め込まれたペイロードを読む実装。
/// リソース名は "payload/manifest.json"、"payload/game/&lt;相対パス&gt;" の形（区切りは "/"）。
/// ビルド環境によって区切りが "\" になる可能性に備え、読み込み時に "/" へ正規化して照合する。
/// </summary>
public sealed class EmbeddedPayloadSource : IPayloadSource
{
    /// <summary>リソース名の接頭辞。</summary>
    public const string ResourcePrefix = "payload/";

    /// <summary>manifest.json のリソース名（正規化後）。</summary>
    private const string ManifestResourceName = ResourcePrefix + "manifest.json";

    /// <summary>リソースが欠けている・開けない場合の案内。</summary>
    private const string RedownloadGuidance = "exe が壊れている可能性があります。配布ページからダウンロードし直してください。";

    private readonly Func<string, Stream?> _openResource;
    private readonly IReadOnlyDictionary<string, string> _resourceNames;

    /// <summary>ペイロードを埋め込んだアセンブリを指定して作る。</summary>
    /// <param name="assembly">リソースを持つアセンブリ。</param>
    /// <exception cref="PatchException">正規化後のリソース名が重複している場合。</exception>
    public EmbeddedPayloadSource(Assembly assembly)
        : this($"埋め込みリソース: {assembly.GetName().Name}", assembly.GetManifestResourceNames(), assembly.GetManifestResourceStream)
    {
    }

    /// <summary>
    /// リソース名の一覧と、リソースを開く処理を直接指定して作る（テストで、区切りの正規化や欠落時の動作を確認するために使う）。
    /// </summary>
    /// <param name="description">取得元の説明。</param>
    /// <param name="resourceNames">リソース名の一覧（区切りは "/" または "\"）。</param>
    /// <param name="openResource">実際のリソース名からストリームを開く処理（無ければ null を返す）。</param>
    /// <exception cref="PatchException">正規化後のリソース名が重複している場合。</exception>
    internal EmbeddedPayloadSource(string description, IEnumerable<string> resourceNames, Func<string, Stream?> openResource)
    {
        Description = description;
        _openResource = openResource;
        _resourceNames = BuildResourceMap(resourceNames);
    }

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>アセンブリにペイロード（manifest.json）が埋め込まれているかを判定する。</summary>
    /// <param name="assembly">確認するアセンブリ。</param>
    /// <exception cref="PatchException">正規化後のリソース名が重複している場合。</exception>
    public static bool HasPayload(Assembly assembly) => HasPayload(assembly.GetManifestResourceNames());

    /// <summary>リソース名の一覧にペイロード（manifest.json）が含まれるかを判定する。</summary>
    /// <param name="resourceNames">リソース名の一覧。</param>
    /// <exception cref="PatchException">正規化後のリソース名が重複している場合。</exception>
    internal static bool HasPayload(IEnumerable<string> resourceNames) =>
        BuildResourceMap(resourceNames).ContainsKey(ManifestResourceName);

    /// <inheritdoc />
    public byte[] ReadManifest() => ReadResource(ManifestResourceName);

    /// <inheritdoc />
    public byte[] ReadGameFile(string relativePath)
    {
        GamePaths.ValidateRelativePath(relativePath);
        return ReadResource(ResourcePrefix + "game/" + relativePath);
    }

    /// <summary>
    /// 正規化したリソース名（区切りを "/" に統一）から実際のリソース名を引く辞書を作る。
    /// 正規化すると同じ名前になるリソースが複数ある場合は、どちらを使うべきか決められないため例外にする。
    /// </summary>
    private static Dictionary<string, string> BuildResourceMap(IEnumerable<string> resourceNames)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var actual in resourceNames)
        {
            var normalized = actual.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!map.TryAdd(normalized, actual))
            {
                throw new PatchException($"埋め込みペイロードのリソース名が重複しています: {normalized}（{map[normalized]} と {actual}）");
            }
        }

        return map;
    }

    /// <summary>リソースを読み出す。</summary>
    private byte[] ReadResource(string normalizedName)
    {
        if (!_resourceNames.TryGetValue(normalizedName, out var actualName))
        {
            throw new PatchException($"埋め込みペイロードに {normalizedName} がありません。{RedownloadGuidance}");
        }

        using var stream = _openResource(actualName)
            ?? throw new PatchException($"埋め込みペイロード {normalizedName} を開けません。{RedownloadGuidance}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
