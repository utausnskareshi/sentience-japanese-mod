using System.Text.Json;

namespace SentienceJP.Core.Payload;

/// <summary>ペイロードに含まれる 1 ファイルの情報。</summary>
/// <param name="Path">ゲームルートからの相対パス（"/" 区切り）。</param>
/// <param name="Sha256">内容の SHA-256（16 進小文字）。</param>
/// <param name="Size">バイト数。</param>
public sealed record PayloadFileEntry(string Path, string Sha256, long Size);

/// <summary>
/// ペイロードの manifest.json の内容（TypeScript 側が生成する契約データ）。
/// </summary>
/// <param name="Schema">形式バージョン。</param>
/// <param name="PatchVersion">パッチのバージョン（例: "0.1.0"）。</param>
/// <param name="TranslationVersion">訳文データのバージョン。</param>
/// <param name="PluginName">plugins.js に登録するプラグイン名。</param>
/// <param name="PluginEntryJson">plugins.js に追加する要素の JSON（1 行の圧縮形式に正規化済み）。</param>
/// <param name="Files">ゲームフォルダに追加するファイル。</param>
/// <param name="KnownPluginsJsSha256">動作確認済みの plugins.js の SHA-256（空なら照合しない）。</param>
/// <param name="KnownBuildIds">動作確認済みの Steam ビルドID（情報表示用）。</param>
public sealed record PayloadManifest(
    int Schema,
    string PatchVersion,
    string TranslationVersion,
    string PluginName,
    string PluginEntryJson,
    IReadOnlyList<PayloadFileEntry> Files,
    IReadOnlyList<string> KnownPluginsJsSha256,
    IReadOnlyList<string> KnownBuildIds);

/// <summary>
/// manifest.json の解析と検証。不正な内容はすべて日本語の <see cref="PatchException"/> として報告する。
/// </summary>
public static class PayloadManifestParser
{
    /// <summary>
    /// manifest.json のバイト列を解析・検証する。
    /// </summary>
    /// <param name="json">manifest.json の内容（UTF-8）。</param>
    /// <exception cref="PatchException">形式が不正な場合。</exception>
    public static PayloadManifest Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new PatchException($"ペイロードの manifest.json を JSON として解析できません: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new PatchException("ペイロードの manifest.json の最上位がオブジェクトではありません。");
            }

            var schema = RequireInt(root, "schema");
            if (schema != GameConstants.ManifestSchemaVersion)
            {
                throw new PatchException($"未対応の manifest.json 形式です（schema={schema}、対応={GameConstants.ManifestSchemaVersion}）。");
            }

            var patchVersion = RequireString(root, "patchVersion");

            // ビルドスクリプトの検査と同じ形式（"0.1.0"・"1.2.3-beta.1" など）に限る。末尾の改行や全角・アラビア数字が混ざると、
            // ウィンドウのタイトルやログに改行が入り、適用済みの版との比較も「形式不明」として誤判定するため（AA-15）
            if (!PatchVersionFormat.IsValid(patchVersion))
            {
                throw new PatchException($"manifest.json の patchVersion の形式が不正です（例: 0.1.0、1.2.3-beta.1）: \"{patchVersion}\"");
            }

            var translationVersion = OptionalString(root, "translationVersion") ?? string.Empty;
            var (pluginName, pluginEntryJson) = ParsePluginEntry(root);
            var files = ParseFiles(root);
            var knownPluginsJs = OptionalStringArray(root, "knownPluginsJsSha256");
            var badHash = knownPluginsJs.FirstOrDefault(h => !Hashing.IsSha256Hex(h));
            if (badHash is not null)
            {
                throw new PatchException($"manifest.json の knownPluginsJsSha256 に不正な値があります: \"{badHash}\"");
            }

            var knownBuildIds = OptionalStringArray(root, "knownBuildIds");
            return new PayloadManifest(schema, patchVersion, translationVersion, pluginName, pluginEntryJson, files, knownPluginsJs, knownBuildIds);
        }
    }

    /// <summary>pluginEntry を検証し、プラグイン名と圧縮形式の JSON を取り出す。</summary>
    private static (string Name, string Json) ParsePluginEntry(JsonElement root)
    {
        if (!root.TryGetProperty("pluginEntry", out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            throw new PatchException("manifest.json に pluginEntry（オブジェクト）がありません。");
        }

        var name = RequireString(entry, "name");
        if (!string.Equals(name, GameConstants.PluginName, StringComparison.Ordinal))
        {
            throw new PatchException($"manifest.json の pluginEntry.name が想定と異なります（\"{name}\"、想定は \"{GameConstants.PluginName}\"）。");
        }

        if (!entry.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True)
        {
            throw new PatchException("manifest.json の pluginEntry.status が true ではありません。");
        }

        if (!entry.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            throw new PatchException("manifest.json の pluginEntry.parameters がオブジェクトではありません。");
        }

        // plugins.js に 1 行で書き込むため、圧縮形式で再シリアライズする。
        // 既定のエンコーダは非 ASCII を \uXXXX にするため、古い JavaScript エンジン（Chromium 59）でも安全に解釈できる。
        var compact = JsonSerializer.Serialize(entry);
        return (name, compact);
    }

    /// <summary>files 配列を検証して取り出す。</summary>
    private static List<PayloadFileEntry> ParseFiles(JsonElement root)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new PatchException("manifest.json に files（配列）がありません。");
        }

        var result = new List<PayloadFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new PatchException("manifest.json の files に、オブジェクトでない要素があります。");
            }

            var path = RequireString(item, "path");
            var problem = GamePaths.DescribeProblem(path);
            if (problem is not null)
            {
                throw new PatchException($"manifest.json の files に不正なパスがあります（{problem}）: \"{path}\"");
            }

            if (IsProtectedPath(path))
            {
                throw new PatchException($"manifest.json の files に、上書きしてはいけないパスがあります: \"{path}\"");
            }

            // 許可リスト方式: パッチが追加してよい場所（プラグインファイルと専用フォルダ配下）以外は受け付けない。
            // 既存のゲームファイルが含まれていると、素のゲームを「不整合」と誤判定し、片付けで本物のファイルを削除してしまうため。
            if (!GamePaths.IsPatchFile(path))
            {
                throw new PatchException(
                    $"manifest.json の files に、パッチが追加できない場所のパスがあります（追加できるのは {GameConstants.PluginFileRelativePath} と "
                    + $"{string.Join("・", GameConstants.PatchOwnedDirectories.Select(d => d + "/ 配下"))}のみ）: \"{path}\"");
            }

            if (!seen.Add(path))
            {
                throw new PatchException($"manifest.json の files にパスが重複しています: \"{path}\"");
            }

            var sha256 = RequireString(item, "sha256");
            if (!Hashing.IsSha256Hex(sha256))
            {
                throw new PatchException($"manifest.json の sha256 が不正です（{path}）: \"{sha256}\"");
            }

            // TryGetInt64 は数値以外（文字列・null 等）で InvalidOperationException を投げるため、先に種類を確認する
            if (!item.TryGetProperty("size", out var sizeElement)
                || sizeElement.ValueKind != JsonValueKind.Number
                || !sizeElement.TryGetInt64(out var size)
                || size < 0)
            {
                throw new PatchException($"manifest.json の size が不正です（{path}）。");
            }

            result.Add(new PayloadFileEntry(path, sha256, size));
        }

        if (result.Count == 0)
        {
            throw new PatchException("manifest.json の files が空です。");
        }

        return result;
    }

    /// <summary>
    /// ペイロードで書き込んではいけないパス（元から存在するゲームのファイルや、バックアップ領域）かを判定する。
    /// パッチは「ファイルの追加」と「plugins.js へのエントリ追加」だけを行う設計のため。
    /// </summary>
    private static bool IsProtectedPath(string path) =>
        string.Equals(path, GameConstants.PluginsJsRelativePath, StringComparison.OrdinalIgnoreCase)
        || GameConstants.RequiredGameFiles.Contains(path, StringComparer.OrdinalIgnoreCase)
        || path.StartsWith(GameConstants.BackupDirectoryName + "/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, GameConstants.BackupDirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>必須の文字列プロパティを取得する。</summary>
    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new PatchException($"manifest.json に文字列の \"{name}\" がありません。");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new PatchException($"manifest.json の \"{name}\" が空です。");
        }

        return text;
    }

    /// <summary>任意の文字列プロパティを取得する（無ければ null）。</summary>
    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>必須の整数プロパティを取得する（"1" のような文字列や小数は受け付けない）。</summary>
    private static int RequireInt(JsonElement element, string name)
    {
        // TryGetInt32 は数値以外（文字列・null 等）で InvalidOperationException を投げるため、先に種類を確認する
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new PatchException($"manifest.json に整数の \"{name}\" がありません。");
        }

        return number;
    }

    /// <summary>任意の文字列配列プロパティを取得する（無ければ空）。</summary>
    private static List<string> OptionalStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new PatchException($"manifest.json の \"{name}\" が配列ではありません。");
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : throw new PatchException($"manifest.json の \"{name}\" に文字列でない要素があります。"))
            .ToList();
    }
}
