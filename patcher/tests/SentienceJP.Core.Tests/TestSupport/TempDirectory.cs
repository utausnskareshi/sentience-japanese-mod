namespace SentienceJP.Core.Tests.TestSupport;

/// <summary>
/// テストごとの一時フォルダ。破棄時に中身ごと削除する（ファイルとフォルダの読み取り専用属性も外す）。
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>一時フォルダを作る。</summary>
    public TempDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "sentience_jp_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    /// <summary>一時フォルダのフルパス。</summary>
    public string FullPath { get; }

    /// <summary>一時フォルダ配下のパスを作る（必要ならサブフォルダも作る）。</summary>
    /// <param name="relative">一時フォルダからの相対パス。</param>
    public string Combine(string relative) => Path.Combine(FullPath, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <inheritdoc />
    public void Dispose()
    {
        if (!Directory.Exists(FullPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(FullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        // 読み取り専用属性の付いたフォルダは削除できないため外す（属性のテストでフォルダにも付けるため）
        foreach (var directory in Directory.EnumerateDirectories(FullPath, "*", SearchOption.AllDirectories))
        {
            new DirectoryInfo(directory).Attributes &= ~FileAttributes.ReadOnly;
        }

        Directory.Delete(FullPath, recursive: true);
    }
}
