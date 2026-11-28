using System.Globalization;
using Microsoft.AspNetCore.Http;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Contracts;

/// <summary>Request helpers of the framework: current user, expected version, paging.</summary>
public static class ApiRequest
{
    internal const string ExpectedVersionKey = "wp.expectedVersion";

    /// <summary>The maximum length of a cursor (Kernel <see cref="CursorCodec.MaxLength"/>).</summary>
    public const int MaxCursorLength = CursorCodec.MaxLength;

    /// <summary>The identity of the request (anonymous outside the API pipeline).</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The current user.</returns>
    public static ICurrentUser CurrentUser(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ICurrentUser>() ?? throw new InvalidOperationException("The API authentication middleware did not run for this request.");
    }

    /// <summary>The version of <c>If-Match</c> on a <c>Versioned()</c> endpoint, <see langword="null"/> when none was given (GET, POST).</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The expected version.</returns>
    public static long? ExpectedVersion(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ExpectedVersionKey, out var value) && value is long version ? version : null;
    }

    /// <summary>Parses an <c>If-Match</c> value <c>"&lt;n&gt;"</c> (or <c>W/"&lt;n&gt;"</c>, or a bare number).</summary>
    /// <param name="header">The header value.</param>
    /// <returns>The version, or <see langword="null"/> when malformed.</returns>
    public static long? ParseETag(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var text = header.Trim();
        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        text = text.Trim('"');
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0 ? version : null;
    }

    /// <summary>
    /// Binds <c>limit</c> (1–1000, default from <c>WithPaging</c>), <c>cursor</c> (≤ 2048 characters) and <c>sort</c>
    /// (validated against <paramref name="sortFields"/>); errors are field errors on <c>/query/…</c>.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="sortFields">The allowed sort fields, or <see langword="null"/> when the endpoint does not sort.</param>
    /// <param name="defaultSort">The default sort order.</param>
    /// <returns>The paging parameters.</returns>
    public static Result<PagingParameters> Paging(this HttpContext context, IReadOnlySet<string>? sortFields = null, SortSpec? defaultSort = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var query = context.Request.Query;
        var defaultLimit = context.GetEndpoint()?.Metadata.GetMetadata<PagingDefaults>()?.DefaultLimit ?? PageRequest.DefaultLimit;
        var errors = new List<FieldError>();

        var limit = defaultLimit;
        if (query.TryGetValue("limit", out var limitText) && !string.IsNullOrEmpty(limitText.ToString()))
        {
            if (!int.TryParse(limitText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > PageRequest.MaxLimit)
            {
                errors.Add(FieldError.Create("/query/limit", CommonErrorCodes.ValueOutOfRange, ("min", 1L), ("max", (long)PageRequest.MaxLimit)));
            }
        }

        var cursor = query.TryGetValue("cursor", out var cursorText) && !string.IsNullOrEmpty(cursorText.ToString()) ? cursorText.ToString() : null;
        if (cursor is { Length: > MaxCursorLength })
        {
            errors.Add(FieldError.Create("/query/cursor", CommonErrorCodes.CursorInvalid));
        }

        SortSpec? sort = defaultSort;
        if (query.TryGetValue("sort", out var sortText) && !string.IsNullOrEmpty(sortText.ToString()))
        {
            if (sortFields is null)
            {
                errors.Add(FieldError.Create("/query/sort", CommonErrorCodes.SortInvalidField, ("field", sortText.ToString())));
            }
            else
            {
                var parsed = SortSpec.Parse(sortText.ToString(), sortFields, defaultSort ?? new SortSpec([]));
                if (parsed.TryGetValue(out var spec, out var error))
                {
                    sort = spec;
                }
                else
                {
                    errors.Add(FieldError.Create("/query/sort", error.Code, [.. error.Args.Select(a => (a.Key, a.Value))]));
                }
            }
        }

        if (errors.Count > 0)
        {
            return CommonErrors.ValidationFailed(errors);
        }

        return new PagingParameters(new PageRequest(limit, cursor), sort);
    }
}

/// <summary>Bound paging parameters.</summary>
/// <param name="Page">Limit and cursor.</param>
/// <param name="Sort">The sort order (the default when none was given).</param>
public sealed record PagingParameters(PageRequest Page, SortSpec? Sort);
