namespace SentienceJP.Core.Tests.TestSupport;

/// <summary>
/// 別のスレッドでファイルを短い間だけ開いておき、他のプログラム（ウイルス対策ソフトのスキャン等）が一瞬ファイルを開いている状況を再現する補助。
/// <see cref="Hold"/> はファイルを開き終えるまで待ってから戻るため、戻った直後の操作は必ずロック中に始まる。
/// 破棄すると、ロックを持つスレッドの終了（ロックの解放）を待つ。
/// </summary>
internal sealed class BriefFileLock : IDisposable
{
    private readonly Thread _thread;

    private BriefFileLock(Thread thread)
    {
        _thread = thread;
    }

    /// <summary>ファイルを指定した共有モードで開き、指定した時間だけ保持する。</summary>
    /// <param name="path">開くファイル。</param>
    /// <param name="share">他に許す共有（FileShare.None で排他）。</param>
    /// <param name="duration">開いておく時間。</param>
    public static BriefFileLock Hold(string path, FileShare share, TimeSpan duration)
    {
        using var opened = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
                opened.Set();
                return;
            }

            using (stream)
            {
                opened.Set();
                Thread.Sleep(duration);
            }
        });
        thread.Start();
        opened.Wait();
        if (failure is not null)
        {
            thread.Join();
            throw new InvalidOperationException($"テスト用のロックを取れませんでした: {path}", failure);
        }

        return new BriefFileLock(thread);
    }

    /// <inheritdoc />
    public void Dispose() => _thread.Join();
}
