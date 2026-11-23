using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SentienceJP.Core.Platform;

/// <summary>ゲームの起動状況。</summary>
public enum GameProcessState
{
    /// <summary>起動していない。</summary>
    NotRunning,

    /// <summary>起動している（対象フォルダの nw.exe が動いている）。</summary>
    Running,

    /// <summary>nw.exe が動いているが、場所を確認できなかった（権限不足など）。</summary>
    Unknown,
}

/// <summary>ゲームの起動状況を調べる抽象。テストでは偽の実装に差し替える。</summary>
public interface IGameProcessChecker
{
    /// <summary>指定したゲームフォルダの nw.exe が起動しているかを調べる。</summary>
    /// <param name="gameDirectory">ゲームのルートフォルダ。</param>
    GameProcessState Check(string gameDirectory);
}

/// <summary>
/// プロセス一覧から、ゲームフォルダ配下の nw.exe が起動しているかを調べる実装。
/// ゲームの nw.exe は 32bit のため、64bit のこのツールからでも確実にパスを得られるよう
/// QueryFullProcessImageName（PROCESS_QUERY_LIMITED_INFORMATION）を使う。
/// ゲームフォルダがジャンクション経由（別ドライブへ移したゲームなど）でも起動中を見落とさないよう、
/// ゲームフォルダとプロセスの実行ファイルの両方を実体のパス（GetFinalPathNameByHandle）に正規化しても比較する（CA-09）。
/// 判定は <see cref="GameProcessPathMatcher.Classify"/>（純粋関数）に任せる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class GameProcessChecker : IGameProcessChecker
{
    /// <summary>プロセス情報の限定的な問い合わせ権限。</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>CreateFile: 既存のファイル・フォルダだけを開く。</summary>
    private const uint OpenExisting = 3;

    /// <summary>CreateFile: フォルダも開けるようにする（FILE_FLAG_BACKUP_SEMANTICS）。</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>CreateFile: 読み取り・書き込み・削除のすべてを共有する（使用中のファイルも開けるように、また他を妨げないように）。</summary>
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;

    /// <summary>GetFinalPathNameByHandle: 正規化した長い名前・ドライブ文字の表記で返す（FILE_NAME_NORMALIZED | VOLUME_NAME_DOS）。</summary>
    private const uint FinalPathNormalizedDos = 0;

    /// <summary>調べるプロセス名（拡張子なし）。</summary>
    private readonly string _processName;

    /// <summary>ゲームの実行ファイル（nw）を調べる、本番用の実装を作る。</summary>
    public GameProcessChecker()
        : this(GameConstants.GameProcessName)
    {
    }

    /// <summary>調べるプロセス名を指定して作る（テストで、実行中のテストプロセス自身を対象にするために使う）。</summary>
    /// <param name="processName">プロセス名（拡張子なし）。</param>
    internal GameProcessChecker(string processName)
    {
        _processName = processName;
    }

    /// <inheritdoc />
    public GameProcessState Check(string gameDirectory)
    {
        // ゲームフォルダの実体のパス（ジャンクション等を解決したもの）。取得できなければ表記どおりの比較だけを行う
        var finalGameDirectory = TryResolveFinalPath(gameDirectory);
        var processes = Process.GetProcessesByName(_processName);
        try
        {
            var unknown = false;
            foreach (var process in processes)
            {
                var path = TryGetImagePath(process.Id);
                if (path is null)
                {
                    unknown = true;
                    continue;
                }

                switch (GameProcessPathMatcher.Classify(path, gameDirectory, finalGameDirectory, TryResolveFinalPath))
                {
                    case GameProcessState.Running:
                        return GameProcessState.Running;
                    case GameProcessState.Unknown:
                        unknown = true;
                        break;
                    case GameProcessState.NotRunning:
                        break;
                    default:
                        throw new InvalidOperationException("未知の起動状況です。");
                }
            }

            return unknown ? GameProcessState.Unknown : GameProcessState.NotRunning;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// プロセスの実行ファイルのフルパスを得る。取得できない場合（権限不足・終了済みなど）は null。
    /// null は呼び出し側で「判定不能（Unknown）」として扱い、利用者に確認を求める。
    /// </summary>
    private static unsafe string? TryGetImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        // 長いパス（\\?\ 形式を含む）にも対応できる大きさのバッファを確保する
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        fixed (char* pointer = buffer)
        {
            return QueryFullProcessImageName(handle, 0, pointer, ref size) ? new string(pointer, 0, (int)size) : null;
        }
    }

    /// <summary>
    /// ファイル・フォルダの実体のパス（ジャンクション・シンボリックリンク・SUBST・8.3 形式の短い名前を解決し、拡張パスの接頭辞を外したもの）を得る。
    /// 取得できない場合（存在しない・開けない・不正なパスなど）は null（呼び出し側で安全側に扱う）。読み取りのみで、何も変更しない。
    /// </summary>
    /// <param name="path">ファイルまたはフォルダのパス。</param>
    internal static unsafe string? TryResolveFinalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        // 属性の問い合わせだけ（アクセス権 0）で開く。フォルダも開けるよう FILE_FLAG_BACKUP_SEMANTICS を付ける
        using var handle = CreateFile(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32768];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, FinalPathNormalizedDos);
        }

        // 0 は失敗、バッファ長以上は足りなかった（必要な長さが返る）ことを示す
        return length == 0 || length >= buffer.Length
            ? null
            : GameProcessPathMatcher.StripExtendedPrefix(new string(buffer, 0, (int)length));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint pathLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);
}
