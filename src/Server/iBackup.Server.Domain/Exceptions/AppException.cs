namespace iBackup.Server.Domain.Exceptions;

/// <summary>
/// Application-level exception carrying an HTTP-mappable status and stable error code.
/// Translated to a JSON <c>ApiError</c> by the API exception middleware.
/// </summary>
public class AppException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public AppException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public static AppException NotFound(string message) => new(404, "not_found", message);
    public static AppException Unauthorized(string message) => new(401, "unauthorized", message);
    public static AppException Forbidden(string message) => new(403, "forbidden", message);
    public static AppException Conflict(string message) => new(409, "conflict", message);
    public static AppException BadRequest(string message) => new(400, "bad_request", message);
    public static AppException QuotaExceeded(string message) => new(413, "quota_exceeded", message);
}
