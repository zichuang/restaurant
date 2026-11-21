namespace Restaurant.Core.Abstractions;

/// <summary>
/// 业务结果。用于「可预期的失败」（沽清、会话过期、参数非法），
/// 不用于异常——真正的异常（数据库连不上）应该抛出并由全局中间件兜底。
/// </summary>
public sealed class Result<T>
{
    private Result(bool ok, T? value, string? code, string? message)
    {
        IsSuccess = ok;
        Value = value;
        ErrorCode = code;
        ErrorMessage = message;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    /// <summary>机器可读的错误码，与 03-api.md 的错误码表一致。</summary>
    public string? ErrorCode { get; }

    /// <summary>给用户看的中文提示。</summary>
    public string? ErrorMessage { get; }

    public static Result<T> Ok(T value) => new(true, value, null, null);

    public static Result<T> Fail(string code, string message) => new(false, default, code, message);
}

public static class Result
{
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Fail<T>(string code, string message) => Result<T>.Fail(code, message);

    public static Result<bool> Ok() => Result<bool>.Ok(true);

    public static Result<bool> Fail(string code, string message) => Result<bool>.Fail(code, message);
}
