using System.IO;
using SentienceJP.Core;
using SentienceJP.Core.Payload;

namespace SentienceJP.Patcher.Services;

/// <summary>
/// ペイロードの取得元を決める。
/// 配布用 exe では埋め込みリソースを使う。開発ビルド（Debug）でペイロードが埋め込まれていない場合に限り、
/// リポジトリの dist/payload を探して使う。
/// </summary>
public static class PayloadSourceFactory
{
    /// <summary>ペイロードの取得元を作る。</summary>
    /// <exception cref="PatchException">ペイロードが見つからない場合。</exception>
    public static IPayloadSource Create()
    {
        var assembly = typeof(PayloadSourceFactory).Assembly;
        if (EmbeddedPayloadSource.HasPayload(assembly))
        {
            return new EmbeddedPayloadSource(assembly);
        }

#if DEBUG
        var directory = FindRepositoryPayload(AppContext.BaseDirectory);
        if (directory is not null)
        {
            return new DirectoryPayloadSource(directory);
        }
#endif

        throw new PatchException("パッチのデータ（ペイロード）が見つかりません。配布されている exe をそのまま使用してください。");
    }

    /// <summary>
    /// 埋め込みリソースのペイロードだけを取得元にする（自己検査用）。
    /// 開発ビルドのフォルダ読み込みに切り替わると、exe 自体の埋め込みを検査したことにならないため、埋め込みが無ければ失敗させる。
    /// </summary>
    /// <exception cref="PatchException">ペイロードが埋め込まれていない場合。</exception>
    public static IPayloadSource CreateEmbedded()
    {
        var assembly = typeof(PayloadSourceFactory).Assembly;
        return EmbeddedPayloadSource.HasPayload(assembly)
            ? new EmbeddedPayloadSource(assembly)
            : throw new PatchException($"この exe にはペイロードが埋め込まれていません（{EmbeddedPayloadSource.ResourcePrefix}manifest.json が見つかりません）。");
    }

#if DEBUG
    /// <summary>実行ファイルの場所から親フォルダをたどり、dist/payload/manifest.json を探す（開発用）。</summary>
    private static string? FindRepositoryPayload(string startDirectory)
    {
        for (var current = new DirectoryInfo(startDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "dist", "payload");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        return null;
    }
#endif
}
