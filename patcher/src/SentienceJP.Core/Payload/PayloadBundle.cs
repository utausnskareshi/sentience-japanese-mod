namespace SentienceJP.Core.Payload;

/// <summary>
/// 検証済みのペイロード一式（manifest と、追加ファイルの内容）。
/// 読み込み時にサイズと SHA-256 を照合するため、このオブジェクトが存在する時点で内容は正しいことが保証される。
/// </summary>
/// <param name="Manifest">manifest.json の内容。</param>
/// <param name="Files">相対パス → ファイル内容。</param>
/// <param name="SourceDescription">取得元の説明（ログ用）。</param>
public sealed record PayloadBundle(
    PayloadManifest Manifest,
    IReadOnlyDictionary<string, byte[]> Files,
    string SourceDescription)
{
    /// <summary>
    /// 取得元からペイロードを読み込み、manifest と各ファイルの整合性を検証する。
    /// </summary>
    /// <param name="source">ペイロードの取得元。</param>
    /// <exception cref="PatchException">manifest が不正、ファイルが無い、サイズやハッシュが一致しない場合。</exception>
    public static PayloadBundle Load(IPayloadSource source)
    {
        var manifest = PayloadManifestParser.Parse(source.ReadManifest());
        var files = manifest.Files.ToDictionary(
            entry => entry.Path,
            entry => VerifyContent(entry, source.ReadGameFile(entry.Path)),
            StringComparer.Ordinal);
        return new PayloadBundle(manifest, files, source.Description);
    }

    /// <summary>ファイル内容が manifest の記載（サイズ・SHA-256）と一致するか検証し、一致すれば内容を返す。</summary>
    private static byte[] VerifyContent(PayloadFileEntry entry, byte[] content)
    {
        if (content.LongLength != entry.Size)
        {
            throw new PatchException($"ペイロードのファイルサイズが一致しません（{entry.Path}: 期待 {entry.Size} バイト、実際 {content.LongLength} バイト）。ダウンロードし直してください。");
        }

        var actual = Hashing.Sha256Hex(content);
        if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
        {
            throw new PatchException($"ペイロードの内容が破損しています（{entry.Path} の SHA-256 が一致しません）。ダウンロードし直してください。");
        }

        return content;
    }
}
