namespace SentienceJP.Core;

/// <summary>
/// パッチ処理で発生した、利用者にそのまま提示できるエラー。
/// <see cref="Exception.Message"/> には日本語の説明文（何が起きたか・どうすればよいか）を入れる。
/// </summary>
public sealed class PatchException : Exception
{
    /// <summary>説明文だけを持つ例外を作る。</summary>
    /// <param name="message">利用者向けの日本語の説明文。</param>
    public PatchException(string message)
        : base(message)
    {
    }

    /// <summary>原因となった例外を伴う例外を作る。</summary>
    /// <param name="message">利用者向けの日本語の説明文。</param>
    /// <param name="innerException">原因となった例外（ログに詳細を残すために保持する）。</param>
    public PatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
