namespace SentienceJP.Core;

/// <summary>
/// よく起きる例外を、日本語の利用者向けの説明に置き換える純粋関数（AA-13）。
/// .NET の例外メッセージは OS の言語に関係なく英語のため、そのまま画面に出すと、日本語の利用者に原因と対処が伝わらない。
/// 英語の原文（ファイルのパスなど具体的な情報を含む）は括弧書きで残し、情報を失わないようにする。
/// 画面のダイアログ・Core の「原因: …」・書き込み権限の確認の理由・ログ書き込み失敗の表示で使う。
/// </summary>
public static class ErrorDescriber
{
    /// <summary>ERROR_SHARING_VIOLATION（32）の HRESULT。</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>ERROR_LOCK_VIOLATION（33）の HRESULT。</summary>
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>ERROR_DISK_FULL（112）の HRESULT。</summary>
    private const int DiskFull = unchecked((int)0x80070070);

    /// <summary>ERROR_HANDLE_DISK_FULL（39）の HRESULT。</summary>
    private const int HandleDiskFull = unchecked((int)0x80070027);

    /// <summary>ERROR_FILENAME_EXCED_RANGE（206。パスが長すぎる）の HRESULT。</summary>
    private const int FileNameTooLong = unchecked((int)0x800700CE);

    /// <summary>ERROR_NOT_READY（21。ドライブの準備ができていない）の HRESULT。</summary>
    private const int NotReady = unchecked((int)0x80070015);

    /// <summary>ERROR_WRITE_PROTECT（19。書き込み禁止のドライブ）の HRESULT。</summary>
    private const int WriteProtect = unchecked((int)0x80070013);

    /// <summary>
    /// 例外を、利用者向けの日本語の説明（英語の原文を括弧書きで添える）にする。
    /// 既に日本語の説明を持つもの（<see cref="PatchException"/>、日本語を含むメッセージ）と、対象外の例外は、メッセージをそのまま返す。
    /// </summary>
    /// <param name="error">説明する例外。</param>
    public static string Describe(Exception error)
    {
        if (error is PatchException || ContainsJapanese(error.Message) || Explain(error) is not { } japanese)
        {
            return error.Message;
        }

        return string.IsNullOrWhiteSpace(error.Message) ? japanese : $"{japanese}（{error.Message}）";
    }

    /// <summary>
    /// よく起きる例外の、日本語の説明だけを返す（原文は含めない。対象外の例外は null）。
    /// 種類（アクセス拒否・ファイルやフォルダが無い等）と、入出力エラーの HRESULT（共有違反・ディスク容量不足等）で判断する。
    /// </summary>
    /// <param name="error">説明する例外。</param>
    public static string? Explain(Exception error) => error switch
    {
        // 他のプログラムが削除の共有なしで開いているファイルを置き換える・削除するときも、Windows は共有違反ではなく
        // アクセス拒否（ERROR_ACCESS_DENIED）を返すため、候補に「他のプログラムがファイルを開いている」も挙げる（LT-02。
        // 閉じれば再実行で成功することを利用者が判断できるように）
        UnauthorizedAccessException => "アクセスが拒否されました（権限が無い、他のプログラムがファイルを開いている、読み取り専用になっている、ウイルス対策ソフトなどがブロックしている可能性があります）",
        FileNotFoundException => "ファイルが見つかりません",
        DirectoryNotFoundException => "フォルダが見つかりません",
        DriveNotFoundException => "ドライブが見つかりません（外付けドライブが外れている可能性があります）",
        PathTooLongException => "パスが長すぎます",
        IOException io => io.HResult switch
        {
            SharingViolation => "他のプログラムがファイルを使用しています（ゲーム・エディタ・ウイルス対策ソフトのスキャンなど）",
            LockViolation => "他のプログラムがファイルの一部をロックしています",
            DiskFull or HandleDiskFull => "ディスクの空き容量が足りません",
            FileNameTooLong => "パスが長すぎます",
            NotReady => "ドライブの準備ができていません（外付けドライブが外れている可能性があります）",
            WriteProtect => "ドライブが書き込み禁止になっています",
            _ => "ファイルの読み書きでエラーが発生しました",
        },
        _ => null,
    };

    /// <summary>
    /// 文字列に日本語（ひらがな・カタカナ・漢字・全角の記号）が含まれるかを判定する純粋関数。
    /// このツール自身が日本語で書いた説明（例外の包み直しなど）や、OS が日本語で返したメッセージを、二重に説明しないために使う。
    /// </summary>
    /// <param name="text">判定する文字列。</param>
    internal static bool ContainsJapanese(string text) =>
        text.Any(c => c is >= (char)0x3000 and <= (char)0x9FFF or >= (char)0xFF00 and <= (char)0xFFEF);
}
