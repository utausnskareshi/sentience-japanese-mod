namespace SentienceJP.Core.Payload;

/// <summary>ペイロードの自己検査の結果。</summary>
/// <param name="ExitCode">プロセスの終了コード（<see cref="PayloadSelfCheck.Success"/> などの値）。</param>
/// <param name="Report">結果の説明（ビルドスクリプトが表示する。複数行）。</param>
public sealed record PayloadSelfCheckResult(int ExitCode, string Report);

/// <summary>
/// 配布用 exe に埋め込んだペイロードを、実際に読み込んで検証する自己検査。
/// ビルドスクリプト（patcher/build.ps1）が publish 後の exe を「--verify-payload」付きで起動し、終了コードで合否を確認する。
/// （dist/payload を手で直して manifest と食い違った場合や、リソース名の埋め込み方が崩れた場合に、配布前に気付くため）
/// 合格時の結果（<see cref="PayloadSelfCheckResult.Report"/>）は次の形式で、ビルドスクリプトと配布 Zip の作成スクリプト（src/tools/package.ts）が
/// 行単位で読む。形式を変えないこと:
/// 1 行目 "OK: …"、2 行目 "patchVersion=&lt;版&gt; translationVersion=&lt;版&gt; files=&lt;数&gt;"、
/// 3 行目 "manifestSha256=&lt;小文字16進64桁&gt;"（埋め込んだ manifest.json の生のバイト列の SHA-256。AA-01）、以降は "  &lt;パス&gt; (…)" のファイル一覧。
/// </summary>
public static class PayloadSelfCheck
{
    /// <summary>検査に合格した。</summary>
    public const int Success = 0;

    /// <summary>ペイロードが無い・壊れている（manifest と内容が一致しない等）。</summary>
    public const int PayloadInvalid = 1;

    /// <summary>結果の、埋め込んだ manifest.json の SHA-256 を示す行の接頭辞（AA-01）。</summary>
    public const string ManifestSha256Prefix = "manifestSha256=";

    /// <summary>
    /// 取得元を作ってペイロードを読み込み、manifest と各ファイルの整合性（サイズ・SHA-256）を検証する。
    /// 合格時は、埋め込んだ manifest.json の生のバイト列の SHA-256 も報告する（ビルドスクリプトが、指定したペイロードの manifest.json と照合し、
    /// 古いビルドの内容が埋め込まれたままの exe を配布しないようにするため。manifest には各ファイルのハッシュが入っているので、これで全体が保証される）。
    /// </summary>
    /// <param name="createSource">ペイロードの取得元を作る処理（取得元が無い場合は <see cref="PatchException"/> を投げる）。</param>
    public static PayloadSelfCheckResult Run(Func<IPayloadSource> createSource)
    {
        try
        {
            var source = createSource();
            var manifestSha256 = Hashing.Sha256Hex(source.ReadManifest());
            var bundle = PayloadBundle.Load(source);
            var manifest = bundle.Manifest;
            var lines = new List<string>
            {
                $"OK: 埋め込みペイロードを検証しました（{bundle.SourceDescription}）。",
                $"patchVersion={manifest.PatchVersion} translationVersion={manifest.TranslationVersion} files={manifest.Files.Count}",
                $"{ManifestSha256Prefix}{manifestSha256}",
            };
            lines.AddRange(manifest.Files.Select(file => $"  {file.Path} ({file.Size} bytes, sha256={file.Sha256})"));
            return new PayloadSelfCheckResult(Success, string.Join(Environment.NewLine, lines));
        }
        catch (PatchException ex)
        {
            return new PayloadSelfCheckResult(PayloadInvalid, $"NG: {ex.Message}");
        }
    }
}
