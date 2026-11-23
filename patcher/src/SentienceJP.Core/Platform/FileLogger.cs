using System.Globalization;
using System.Text;
using SentienceJP.Core.Patching;

namespace SentienceJP.Core.Platform;

/// <summary>
/// 日付ごとのファイルにログを追記する。
/// 既定の保存先: %LOCALAPPDATA%\SentienceJP\logs\patcher-yyyyMMdd.log
/// ログの書き込みに失敗してもツールの動作は止めないが、失敗したことは <see cref="LastError"/> で参照できる。
/// 一時的な失敗（別のプロセスの同時追記・ウイルス対策ソフトの検査による共有違反）は短い再試行で吸収し、
/// 失敗の後でも書き込めた時点で <see cref="LastError"/> を消す（CA-17。以降は通常どおりログの場所を案内する）。
/// </summary>
public sealed class FileLogger
{
    /// <summary>
    /// ログの書き込みの共有違反に対する短い再試行（合計 0.3 秒まで）。
    /// ログは画面の処理（UI スレッド）から書くため、待ち時間は適用・復元のファイル操作より短くする。
    /// </summary>
    private static readonly SharingViolationRetry WriteRetry = new(
    [
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(80),
        TimeSpan.FromMilliseconds(160),
    ]);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly TimeProvider _clock;

    /// <summary>保存先フォルダを指定して作る。</summary>
    /// <param name="directory">ログファイルを置くフォルダ。</param>
    /// <param name="clock">時刻の取得元（省略時はシステム時刻）。</param>
    public FileLogger(string directory, TimeProvider? clock = null)
    {
        _directory = directory;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>既定の保存先のロガーを作る。</summary>
    public static FileLogger CreateDefault() =>
        new(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SentienceJP", "logs"));

    /// <summary>
    /// 最後に発生したログ書き込みの失敗内容（無ければ null）。
    /// 失敗の後に書き込めた場合は消える（一時的な失敗のあと、存在するログファイルの場所を案内できるようにするため。CA-17）。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>現在のログファイルのパス。</summary>
    public string CurrentLogPath =>
        Path.Combine(_directory, $"patcher-{_clock.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    /// <summary>
    /// ダイアログに添える、ログの場所の案内。
    /// ログの書き込みに失敗している場合は、存在しない（または不完全な）ログファイルを案内しないよう、失敗した旨を伝える。
    /// </summary>
    /// <param name="logViewAvailable">画面にログ欄があるか（ある場合は、書き込みに失敗したときにそちらを案内する）。</param>
    public string DescribeLocation(bool logViewAvailable = false)
    {
        if (LastError is not { } error)
        {
            return $"ログ: {CurrentLogPath}";
        }

        return logViewAvailable
            ? $"{error}{System.Environment.NewLine}（詳しい記録は、この画面のログ欄を確認してください）"
            : error;
    }

    /// <summary>情報ログを書く。</summary>
    /// <param name="message">内容。</param>
    public void Info(string message) => Write("INFO", message);

    /// <summary>警告ログを書く。</summary>
    /// <param name="message">内容。</param>
    public void Warn(string message) => Write("WARN", message);

    /// <summary>エラーログを書く（例外があれば詳細も書く）。</summary>
    /// <param name="message">内容。</param>
    /// <param name="exception">原因の例外。</param>
    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{System.Environment.NewLine}{exception}");

    /// <summary>1 行（複数行可）のログを追記する。</summary>
    private void Write(string level, string message)
    {
        var line = $"{_clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{System.Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                // 直前の書き込みが失敗している間は再試行しない（書けない状態が続くときに、ログを書くたびに画面の処理を待たせないため）
                var retry = LastError is null ? WriteRetry : SharingViolationRetry.None;
                retry.Run(() =>
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
                });

                // 書き込めたら、以前の失敗の記録を消す（以降は通常どおりログの場所を案内する。CA-17）
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ログの失敗で本来の処理を止めないが、失敗した事実は呼び出し側から参照できるように残す（画面に出るため日本語の説明にする。AA-13）
                LastError = $"ログを書き込めませんでした（{CurrentLogPath}）: {ErrorDescriber.Describe(ex)}";
            }
        }
    }
}
