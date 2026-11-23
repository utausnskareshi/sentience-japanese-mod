namespace SentienceJP.Core.Platform;

/// <summary>
/// 名前付き Mutex による多重起動の防止。
/// 2 つの画面から同じゲームフォルダを同時に操作すると、一方のロールバックが他方のバックアップを消すなどの事故が起きうるため、
/// パッチツールは 1 つだけ起動できるようにする。
/// 取得したスレッドで <see cref="Dispose"/> すること（Mutex は所有したスレッドでしか解放できないため）。
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    /// <summary>既存の Mutex を開く権限が無いときに、開き直すまでの間隔（AA-11）。</summary>
    private static readonly TimeSpan AccessDeniedRetryInterval = TimeSpan.FromMilliseconds(200);

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// ロックの取得を試みる。他のインスタンスが持っている（待ち時間内に解放されない）場合は null を返す。
    /// </summary>
    /// <param name="name">Mutex の名前（"Global\" で始めると、別のサインインセッションとも排他する）。</param>
    /// <param name="timeout">他のインスタンスの終了を待つ時間（管理者としての再起動時に、前のインスタンスの終了を待つために使う）。</param>
    /// <returns>取得できたロック。取得できなければ null。</returns>
    public static SingleInstanceLock? TryAcquire(string name, TimeSpan timeout) =>
        TryAcquire(name, timeout, static mutexName => new Mutex(initiallyOwned: false, mutexName), AccessDeniedRetryInterval);

    /// <summary>
    /// 既存の Mutex を開けなかった（アクセス拒否）ときに、次に開き直すまで待つ時間を決める純粋関数（AA-11）。
    /// 待ち時間（<paramref name="timeout"/>）が残っていれば、間隔と残り時間の短い方を返し、残っていなければ null（＝あきらめる）を返す。
    /// 通常の起動（待ち時間 0）は、従来どおり即座に「既に起動している」と判定する（null）。
    /// </summary>
    /// <param name="timeout">待つことを許された時間。</param>
    /// <param name="elapsed">これまでに経過した時間。</param>
    /// <param name="interval">開き直す間隔。</param>
    internal static TimeSpan? NextAccessDeniedRetryDelay(TimeSpan timeout, TimeSpan elapsed, TimeSpan interval)
    {
        var remaining = timeout - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        return remaining < interval ? remaining : interval;
    }

    /// <summary>
    /// ロックの取得を試みる（Mutex の開き方と、アクセス拒否時に開き直す間隔を差し替えられる版。テストで本物の名前を使わずに確かめるために使う）。
    /// </summary>
    /// <param name="name">Mutex の名前。</param>
    /// <param name="timeout">他のインスタンスの終了を待つ時間。</param>
    /// <param name="openMutex">名前から Mutex を作る・開く処理（権限が無ければ <see cref="UnauthorizedAccessException"/> を投げる）。</param>
    /// <param name="retryInterval">アクセス拒否のときに開き直す間隔。</param>
    /// <returns>取得できたロック。取得できなければ null。</returns>
    internal static SingleInstanceLock? TryAcquire(string name, TimeSpan timeout, Func<string, Mutex> openMutex, TimeSpan retryInterval)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Mutex mutex;
        while (true)
        {
            try
            {
                mutex = openMutex(name);
                break;
            }
            catch (UnauthorizedAccessException)
            {
                // 別のユーザー（管理者としての再起動で、別の管理者アカウントの資格情報を入力した場合など）として動いている
                // 前のインスタンスが作った Mutex は、既定のアクセス許可では開けない。
                // 待ち時間の指定がある（管理者としての再起動）なら、前のインスタンスが終了して Mutex が消えるまで、一定間隔で開き直す（AA-11）。
                // 待ち時間 0（通常の起動）や、待ち時間を使い切った場合は、従来どおり「既に起動している」と判定する。
                if (NextAccessDeniedRetryDelay(timeout, stopwatch.Elapsed, retryInterval) is not { } delay)
                {
                    return null;
                }

                Thread.Sleep(delay);
            }
        }

        // Mutex を開くまでに使った分を差し引いた残りの時間だけ、前のインスタンスの解放を待つ
        var remainingWait = timeout - stopwatch.Elapsed;
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(remainingWait > TimeSpan.Zero ? remainingWait : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // 前のインスタンスが解放せずに終了した（強制終了など）。所有権はこのスレッドに引き継がれている。
                acquired = true;
            }

            if (acquired)
            {
                return new SingleInstanceLock(mutex);
            }

            mutex.Dispose();
            return null;
        }
        catch
        {
            // 想定外の失敗では Mutex のハンドルを閉じてから、例外をそのまま呼び出し元へ伝える
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>ロックを解放する（取得したスレッドで呼ぶこと）。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
