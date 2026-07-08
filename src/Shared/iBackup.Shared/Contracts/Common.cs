namespace iBackup.Shared.Contracts;

/// <summary>Standard error payload returned by the API for all non-2xx responses.</summary>
public sealed record ApiError(string Code, string Message, IDictionary<string, string[]>? Details = null);

/// <summary>Generic page of results.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
