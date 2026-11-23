using SentienceJP.Core.Patching;

namespace SentienceJP.Core.Tests.TestSupport;

/// <summary>ファイル操作の種類（失敗を注入する対象の指定に使う。テストのパラメーターに使うため public）。</summary>
public enum FileOperationKind
{
    /// <summary>ファイルの読み取り。</summary>
    Read,

    /// <summary>フォルダの作成。</summary>
    CreateDirectory,

    /// <summary>新しいファイルの作成。</summary>
    WriteNewFile,

    /// <summary>ファイルの置き換え。</summary>
    ReplaceFile,

    /// <summary>ファイルの削除。</summary>
    DeleteFile,

    /// <summary>空のフォルダの削除。</summary>
    DeleteDirectoryIfEmpty,

    /// <summary>フォルダの中身ごとの削除。</summary>
    DeleteDirectoryRecursive,
}

/// <summary>注入した失敗であることを示す例外（本物の入出力エラーと区別するため）。</summary>
internal sealed class InjectedFailureException : IOException
{
    /// <summary>説明を指定して作る。</summary>
    public InjectedFailureException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 実際のファイル操作を包み、指定した操作で失敗させたり、操作の直後に別の変更を差し込んだりするテスト用の実装。
/// - <see cref="FailWhen"/>: 条件に合う操作を、実行せずに失敗させる（その操作だけが失敗した状況）。
/// - <see cref="CrashAfterMutations"/>: 指定した回数の書き込み系操作の後は、読み取りも含めてすべて失敗させる
///   （プロセスが強制終了・電源断した状況の模擬。取り消しも実行できないため、ディスクはその時点の状態で残る）。
/// - <see cref="After"/>: 操作が成功した直後に呼ぶ（同時に他のプログラムがファイルを変更した状況の再現に使う）。
/// </summary>
internal sealed class FaultInjectingFileSystem : IPatchFileSystem
{
    private readonly IPatchFileSystem _inner;
    private int _mutations;

    /// <summary>包む実装を指定して作る（省略時は再試行なしの実際のファイル操作）。</summary>
    public FaultInjectingFileSystem(IPatchFileSystem? inner = null)
    {
        _inner = inner ?? new FileOperations(SharingViolationRetry.None);
    }

    /// <summary>失敗させる操作の条件（種類・対象のフルパス・同じ対象への同じ種類の操作の何回目か（1 から））。</summary>
    public Func<FileOperationKind, string, int, bool> FailWhen { get; set; } = (_, _, _) => false;

    /// <summary>この回数の書き込み系操作が成功した後は、すべての操作を失敗させる（null なら無効）。</summary>
    public int? CrashAfterMutations { get; set; }

    /// <summary>操作が成功した直後に呼ぶ処理（種類・対象のフルパス）。</summary>
    public Action<FileOperationKind, string>? After { get; set; }

    /// <summary>実行（または失敗）した操作の記録。</summary>
    public List<(FileOperationKind Kind, string Path)> Operations { get; } = [];

    /// <summary>成功した書き込み系操作の回数。</summary>
    public int MutationCount => _mutations;

    /// <inheritdoc />
    public byte[] ReadAllBytes(string fullPath) => Run(FileOperationKind.Read, fullPath, () => _inner.ReadAllBytes(fullPath));

    /// <inheritdoc />
    public void CreateDirectory(string fullPath) => Run(FileOperationKind.CreateDirectory, fullPath, () =>
    {
        _inner.CreateDirectory(fullPath);
        return true;
    });

    /// <inheritdoc />
    public void WriteNewFile(string fullPath, byte[] content) => Run(FileOperationKind.WriteNewFile, fullPath, () =>
    {
        _inner.WriteNewFile(fullPath, content);
        return true;
    });

    /// <inheritdoc />
    public void ReplaceFile(string fullPath, byte[] content) => Run(FileOperationKind.ReplaceFile, fullPath, () =>
    {
        _inner.ReplaceFile(fullPath, content);
        return true;
    });

    /// <inheritdoc />
    public void DeleteFile(string fullPath) => Run(FileOperationKind.DeleteFile, fullPath, () =>
    {
        _inner.DeleteFile(fullPath);
        return true;
    });

    /// <inheritdoc />
    public bool DeleteDirectoryIfEmpty(string fullPath) =>
        Run(FileOperationKind.DeleteDirectoryIfEmpty, fullPath, () => _inner.DeleteDirectoryIfEmpty(fullPath));

    /// <inheritdoc />
    public void DeleteDirectoryRecursive(string fullPath) => Run(FileOperationKind.DeleteDirectoryRecursive, fullPath, () =>
    {
        _inner.DeleteDirectoryRecursive(fullPath);
        return true;
    });

    /// <summary>失敗の注入を判定してから操作を実行する。</summary>
    private T Run<T>(FileOperationKind kind, string fullPath, Func<T> operation)
    {
        Operations.Add((kind, fullPath));
        if (CrashAfterMutations is { } limit && _mutations >= limit)
        {
            throw new InjectedFailureException($"（模擬的な強制終了の後の操作）{kind}: {fullPath}");
        }

        var occurrence = Operations.Count(o => o.Kind == kind && string.Equals(o.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (FailWhen(kind, fullPath, occurrence))
        {
            throw new InjectedFailureException($"（注入した失敗）{kind}: {fullPath}");
        }

        var result = operation();
        if (kind != FileOperationKind.Read)
        {
            _mutations++;
        }

        After?.Invoke(kind, fullPath);
        return result;
    }
}
