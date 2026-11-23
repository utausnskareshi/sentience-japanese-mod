namespace SentienceJP.Core.Patching;

/// <summary>
/// 適用・復元が行うファイル操作の差し込み口。
/// 本番では <see cref="FileOperations"/> を使い、テストでは失敗を注入する実装に差し替えて、
/// 各段階で失敗・中断したときのロールバックや、中断状態からの回復を検証する。
/// 読み取り専用の確認（存在確認・一覧）は副作用が無いため対象にせず、内容の読み取りと書き込み系の操作だけを扱う。
/// </summary>
internal interface IPatchFileSystem
{
    /// <summary>ファイルの内容を読む。</summary>
    /// <param name="fullPath">読むファイル。</param>
    byte[] ReadAllBytes(string fullPath);

    /// <summary>フォルダを作る（既にあれば何もしない）。</summary>
    /// <param name="fullPath">作るフォルダ。</param>
    void CreateDirectory(string fullPath);

    /// <summary>新しいファイルを作って書き込み、ディスクへ確実に書き出す。同名のファイルが既にあれば失敗する。</summary>
    /// <param name="fullPath">作るファイル。</param>
    /// <param name="content">書き込む内容。</param>
    void WriteNewFile(string fullPath, byte[] content);

    /// <summary>
    /// ファイルを一時ファイル経由で置き換える（無ければ新しく作る）。
    /// 置き換え後のファイルには元のファイルの属性（読み取り専用・隠し等）を引き継ぐ。
    /// </summary>
    /// <param name="fullPath">置き換えるファイル。</param>
    /// <param name="content">新しい内容。</param>
    void ReplaceFile(string fullPath, byte[] content);

    /// <summary>ファイルを削除する。読み取り専用でも削除し、無ければ何もしない。</summary>
    /// <param name="fullPath">削除するファイル。</param>
    void DeleteFile(string fullPath);

    /// <summary>
    /// フォルダが存在し、かつ空であれば削除する（読み取り専用属性が付いていても削除する）。
    /// リパースポイント（ジャンクション・シンボリックリンク）のフォルダは、パッチが作ったフォルダではないため削除しない。
    /// </summary>
    /// <param name="fullPath">削除するフォルダ。</param>
    /// <returns>削除した場合は true。</returns>
    bool DeleteDirectoryIfEmpty(string fullPath);

    /// <summary>フォルダを中身ごと削除する（読み取り専用のファイル・フォルダも削除する）。</summary>
    /// <param name="fullPath">削除するフォルダ。</param>
    void DeleteDirectoryRecursive(string fullPath);
}

/// <summary>
/// 適用・復元で使う実際のファイル操作。
/// - 書き込みは WriteThrough で行い、ディスクへのフラッシュまで済ませてから次へ進む（直後の電源断で中身が失われるのを防ぐ）。
/// - 既存ファイルの置き換えは「同じフォルダの一時ファイルに書く → 置き換え」で行い、途中で失敗しても元のファイルを壊さない。
/// - 読み取り専用属性が付いていても置き換え・削除できるようにし、置き換えた場合は元の属性を付け直す。
/// - 他のプログラム（ウイルス対策ソフト等）が一瞬ファイルを開いていることによる共有違反は、短い間隔で数回再試行する。
/// 副作用はこのクラスと呼び出し側の手続きに閉じ込め、判定ロジックとは分離する。
/// </summary>
internal sealed class FileOperations : IPatchFileSystem
{
    /// <summary>
    /// 置き換え後のファイルへ引き継ぐ属性（元のファイルと同じ属性に戻すため）。
    /// 圧縮・暗号化などの属性は SetAttributes で設定できないため対象にしない（同じフォルダに作る一時ファイルは同じ設定を継承する）。
    /// </summary>
    internal const FileAttributes PreservedAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    /// <summary>フォルダの再帰処理で、リンク先（ジャンクション・シンボリックリンク）へ入り込まないための列挙設定。</summary>
    private static readonly EnumerationOptions RecursiveWithoutLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    private readonly SharingViolationRetry _retry;

    /// <summary>共有違反の再試行方針を指定して作る。</summary>
    /// <param name="retry">再試行の方針。</param>
    public FileOperations(SharingViolationRetry retry)
    {
        _retry = retry;
    }

    /// <summary>本番用の既定のインスタンス（共有違反は短く再試行する）。</summary>
    public static FileOperations Default { get; } = new(SharingViolationRetry.Default);

    /// <summary>置き換えに使う一時ファイルのパス（対象と同じフォルダ）を返す。</summary>
    /// <param name="fullPath">置き換える対象のファイル。</param>
    public static string TemporaryPathFor(string fullPath) => fullPath + GameConstants.TemporaryFileSuffix;

    /// <summary>
    /// 一時ファイルの属性を、元のファイルの属性（<see cref="PreservedAttributes"/> の範囲）に揃えた値を返す純粋関数。
    /// 何も残らない場合は <see cref="FileAttributes.Normal"/>（単独でのみ有効な値）にする。
    /// </summary>
    /// <param name="original">元のファイルの属性。</param>
    internal static FileAttributes PreservedPart(FileAttributes original)
    {
        var preserved = original & PreservedAttributes;
        return preserved == 0 ? FileAttributes.Normal : preserved;
    }

    /// <inheritdoc />
    public byte[] ReadAllBytes(string fullPath) => _retry.Run(() => File.ReadAllBytes(fullPath));

    /// <inheritdoc />
    public void CreateDirectory(string fullPath) => _retry.Run(() => Directory.CreateDirectory(fullPath));

    /// <inheritdoc />
    public void WriteNewFile(string fullPath, byte[] content) => _retry.Run(() => WriteDurably(fullPath, content));

    /// <inheritdoc />
    public void ReplaceFile(string fullPath, byte[] content)
    {
        var temporaryPath = TemporaryPathFor(fullPath);

        // 前回の中断で残った一時ファイルがあれば先に片付ける（読み取り専用でも消す）
        DeleteFile(temporaryPath);

        var readOnlyCleared = false;
        FileAttributes? target = null;
        try
        {
            _retry.Run(() => WriteDurably(temporaryPath, content));

            if (File.Exists(fullPath))
            {
                var original = File.GetAttributes(fullPath);

                // 一時ファイルに元の属性を付けておき、置き換えと同時に読み取り専用・隠し属性なども元のファイルと同じにする
                var preserved = PreservedPart(original);
                target = preserved;
                if ((File.GetAttributes(temporaryPath) & PreservedAttributes) != (preserved & PreservedAttributes))
                {
                    _retry.Run(() => File.SetAttributes(temporaryPath, preserved));
                }

                // 読み取り専用のファイルは置き換え先にできないため、一時的に外す（失敗時は下の catch で付け直す）
                if (original.HasFlag(FileAttributes.ReadOnly))
                {
                    _retry.Run(() => File.SetAttributes(fullPath, original & ~FileAttributes.ReadOnly));
                    readOnlyCleared = true;
                }
            }

            // 置き換え先を他のプログラム（ウイルス対策ソフトのスキャン等）が開いていると、File.Move の上書きは共有違反ではなく
            // アクセス拒否（UnauthorizedAccessException）になるため、この段に限りアクセス拒否も既定の待ち時間で再試行する（CA-03）
            _retry.RunRetryingAccessDenied(() => File.Move(temporaryPath, fullPath, overwrite: true));
        }
        catch (Exception ex)
        {
            // 置き換えに失敗した場合、元のファイルは無傷のまま残っている。外した読み取り専用属性を戻し、一時ファイルを片付ける。
            // 後片付けにも失敗した場合は、その内容も含めて呼び出し元へ伝える（握りつぶさない）。
            var cleanupProblems = new List<string>();
            if (readOnlyCleared)
            {
                TryRun(
                    () => File.SetAttributes(fullPath, File.GetAttributes(fullPath) | FileAttributes.ReadOnly),
                    $"読み取り専用属性の復元（{fullPath}）",
                    cleanupProblems);
            }

            TryRun(() => DeleteFile(temporaryPath), $"一時ファイルの削除（{temporaryPath}）", cleanupProblems);
            if (cleanupProblems.Count > 0)
            {
                throw new IOException($"{ErrorDescriber.Describe(ex)}（後片付けにも失敗しました: {string.Join("、", cleanupProblems)}）", ex);
            }

            throw;
        }

        // 名前の変更（置き換え）でアーカイブ属性が付くため、元のファイルと異なる場合は付け直す。
        // 内容の置き換えは済んでいるので、ここで失敗した場合はその旨が分かる説明で伝える（呼び出し側の取り消しは内容を見て判断する）。
        if (target is { } expected)
        {
            try
            {
                if ((File.GetAttributes(fullPath) & PreservedAttributes) != (expected & PreservedAttributes))
                {
                    _retry.Run(() => File.SetAttributes(fullPath, expected));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"ファイルの内容は置き換えましたが、属性を元のファイルと同じに戻せませんでした（{fullPath}）: {ErrorDescriber.Describe(ex)}", ex);
            }
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string fullPath) => _retry.Run(() =>
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        ClearReadOnly(fullPath);
        File.Delete(fullPath);
    });

    /// <inheritdoc />
    public bool DeleteDirectoryIfEmpty(string fullPath) => _retry.Run(() =>
    {
        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        // リパースポイント（ジャンクション・シンボリックリンク）は削除しない（CA-01 の多層防御）。
        // 中身の確認もリンク先を見てしまうため、列挙より前に判定する。属性はリンク先ではなくリンク自体のものを見る。
        if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        if (Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            return false;
        }

        // 読み取り専用属性の付いたフォルダは削除できないため外す
        ClearReadOnly(fullPath);
        Directory.Delete(fullPath);
        return true;
    });

    /// <inheritdoc />
    public void DeleteDirectoryRecursive(string fullPath) => _retry.Run(() =>
    {
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        // 中のファイル・フォルダとフォルダ自身の読み取り専用属性を外してから削除する
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath, "*", RecursiveWithoutLinks))
        {
            ClearReadOnly(entry);
        }

        ClearReadOnly(fullPath);
        Directory.Delete(fullPath, recursive: true);
    });

    /// <summary>新しいファイルを WriteThrough で書き、ディスクへのフラッシュまで行う（既にあれば失敗する）。</summary>
    private static void WriteDurably(string fullPath, byte[] content)
    {
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>ファイルまたはフォルダの読み取り専用属性を外す（付いていなければ何もしない）。</summary>
    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>後片付けの処理を実行し、失敗した場合は内容を記録する（主処理の例外を優先して伝えるため、ここでは投げない）。</summary>
    private static void TryRun(Action action, string description, List<string> problems)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{description}: {ErrorDescriber.Describe(ex)}");
        }
    }
}

/// <summary>
/// 一時的な共有違反（他のプログラムがファイルを開いている）に対する、短い再試行の方針。
/// ウイルス対策ソフトやインデクサは書き込み直後のファイルを一瞬開くことがあるため、数回だけ待ってから再試行する。
/// 対象は ERROR_SHARING_VIOLATION（32）と ERROR_LOCK_VIOLATION（33）のみで、それ以外の失敗はすぐに伝える。
/// ただし <see cref="RunRetryingAccessDenied"/> は、アクセス拒否も再試行の対象にする（置き換えの段で使う）。
/// </summary>
internal sealed class SharingViolationRetry
{
    /// <summary>ERROR_SHARING_VIOLATION（32）の HRESULT。</summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    /// <summary>ERROR_LOCK_VIOLATION（33）の HRESULT。</summary>
    private const int LockViolationHResult = unchecked((int)0x80070021);

    private readonly IReadOnlyList<TimeSpan> _delays;

    /// <summary>再試行前の待ち時間の列を指定して作る（要素数が再試行の回数になる）。</summary>
    /// <param name="delays">各再試行の前に待つ時間。</param>
    public SharingViolationRetry(IReadOnlyList<TimeSpan> delays)
    {
        _delays = delays;
    }

    /// <summary>本番用の既定値（合計 0.75 秒まで待つ）。</summary>
    public static SharingViolationRetry Default { get; } = new(
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    ]);

    /// <summary>再試行しない方針。</summary>
    public static SharingViolationRetry None { get; } = new([]);

    /// <summary>HRESULT が一時的な共有違反（32・33）を表すかを判定する純粋関数。</summary>
    /// <param name="hresult">例外の HResult。</param>
    public static bool IsSharingViolation(int hresult) => hresult is SharingViolationHResult or LockViolationHResult;

    /// <summary>
    /// 失敗が再試行の対象かを判定する純粋関数。
    /// 共有違反（32・33）の <see cref="IOException"/> は常に対象。<paramref name="alsoRetryAccessDenied"/> が true なら、
    /// アクセス拒否（<see cref="UnauthorizedAccessException"/>）も対象にする
    /// （置き換え先を他のプログラムが開いていると、File.Move の上書きは共有違反ではなくアクセス拒否になるため。CA-03）。
    /// </summary>
    /// <param name="error">発生した例外。</param>
    /// <param name="alsoRetryAccessDenied">アクセス拒否も再試行するか。</param>
    public static bool ShouldRetry(Exception error, bool alsoRetryAccessDenied) => error switch
    {
        IOException io => IsSharingViolation(io.HResult),
        UnauthorizedAccessException => alsoRetryAccessDenied,
        _ => false,
    };

    /// <summary>処理を実行し、共有違反で失敗した場合は待ってから再試行する。</summary>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="operation">実行する処理。</param>
    public T Run<T>(Func<T> operation) => Run(operation, alsoRetryAccessDenied: false);

    /// <summary>結果を返さない処理を、共有違反時の再試行つきで実行する。</summary>
    /// <param name="operation">実行する処理。</param>
    public void Run(Action operation) => Run(() =>
    {
        operation();
        return true;
    });

    /// <summary>
    /// 結果を返さない処理を、共有違反とアクセス拒否の両方を再試行の対象にして実行する（ファイルの置き換えの段で使う。CA-03）。
    /// 本当に権限が無い場合は、待ち時間（既定で合計 0.75 秒）の後に元の例外をそのまま伝える。
    /// </summary>
    /// <param name="operation">実行する処理。</param>
    public void RunRetryingAccessDenied(Action operation) => Run(
        () =>
        {
            operation();
            return true;
        },
        alsoRetryAccessDenied: true);

    /// <summary>処理を実行し、再試行の対象（<see cref="ShouldRetry"/>）の失敗であれば待ってから再試行する。</summary>
    private T Run<T>(Func<T> operation, bool alsoRetryAccessDenied)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (attempt < _delays.Count && ShouldRetry(ex, alsoRetryAccessDenied))
            {
                // 再試行の回数が残っている間だけ待って続ける。使い切った場合は例外をそのまま呼び出し元へ伝える。
                Thread.Sleep(_delays[attempt]);
            }
        }
    }
}
