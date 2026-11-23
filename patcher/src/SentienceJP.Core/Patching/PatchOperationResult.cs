namespace SentienceJP.Core.Patching;

/// <summary>
/// 適用・復元処理の結果。例外を呼び出し元へ投げる代わりに、成否と説明を値として返す。
/// 失敗時も原因の例外（<see cref="Error"/>）を保持し、ログに詳細を残せるようにする（握りつぶさない）。
/// </summary>
/// <param name="Success">成功したか。</param>
/// <param name="Message">利用者向けの日本語の説明。</param>
/// <param name="Error">失敗の原因となった例外（成功時は null）。</param>
/// <param name="CleanupErrors">ロールバック等の後始末で発生した問題（あれば手動対応が必要）。</param>
public sealed record PatchOperationResult(bool Success, string Message, Exception? Error, IReadOnlyList<string> CleanupErrors)
{
    /// <summary>成功の結果を作る。</summary>
    /// <param name="message">利用者向けの説明。</param>
    public static PatchOperationResult Succeeded(string message) => new(true, message, null, []);

    /// <summary>失敗の結果を作る。</summary>
    /// <param name="message">利用者向けの説明。</param>
    /// <param name="error">原因の例外。</param>
    /// <param name="cleanupErrors">後始末で発生した問題。</param>
    public static PatchOperationResult Failed(string message, Exception error, IReadOnlyList<string> cleanupErrors) =>
        new(false, message, error, cleanupErrors);
}
