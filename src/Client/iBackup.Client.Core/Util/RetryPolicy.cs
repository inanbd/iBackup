using Microsoft.Extensions.Logging;

namespace iBackup.Client.Core.Util;

/// <summary>Exponential backoff with jitter for transient failures.</summary>
public static class RetryPolicy
{
    /// <summary>
    /// Runs <paramref name="action"/> with up to <paramref name="maxAttempts"/> attempts.
    /// Delays grow as baseDelay * 2^(attempt-1) with up to 20% jitter.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        int maxAttempts,
        TimeSpan baseDelay,
        ILogger logger,
        string operationName,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (1 + Random.Shared.NextDouble() * 0.2));
                logger.LogWarning(ex, "{Operation} failed (attempt {Attempt}/{Max}); retrying in {Delay}",
                    operationName, attempt, maxAttempts, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        int maxAttempts,
        TimeSpan baseDelay,
        ILogger logger,
        string operationName,
        CancellationToken ct)
        => ExecuteAsync<object?>(async token =>
        {
            await action(token);
            return null;
        }, maxAttempts, baseDelay, logger, operationName, ct);

    private static bool IsTransient(Exception ex) => ex
        is HttpRequestException
        or IOException
        or TaskCanceledException // HttpClient timeout
        or TimeoutException;
}
