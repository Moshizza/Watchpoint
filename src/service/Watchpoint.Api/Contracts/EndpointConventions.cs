using Microsoft.AspNetCore.Builder;
using Watchpoint.Api.Internal.Pipeline;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Api.Contracts;

/// <summary>Endpoint conventions of the REST framework (spec API §5.1). Each adds metadata read by one framework filter.</summary>
public static class EndpointConventions
{
    /// <summary>The rate limit policy name of <see cref="Heavy{TBuilder}"/> (API §4.2).</summary>
    public const string HeavyPolicy = "heavy";

    /// <summary>Requires <paramref name="permission"/> (API §5.2): anonymous → 401, missing → 403 <c>common.forbidden</c>.</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <param name="permission">The permission code.</param>
    /// <returns>The builder.</returns>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(new PermissionRequirement(PermissionCode.FromTrusted(permission)));

    /// <summary>Requires an authenticated user, without a specific permission (e.g. <c>/auth/me</c>, own jobs) → 401 when anonymous.</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <returns>The builder.</returns>
    public static TBuilder RequireAuthentication<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(AuthenticationRequired.Instance);

    /// <summary>Requires the <c>desktop</c> client kind (API §5.3) and marks the operation <c>x-wp-config</c>.</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <returns>The builder.</returns>
    public static TBuilder ConfigurationEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(ConfigurationEndpointMarker.Instance);

    /// <summary>Restricts the endpoint to the listed client kinds (403 <c>api.client_kind.not_allowed</c>).</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <param name="kinds">The allowed kinds.</param>
    /// <returns>The builder.</returns>
    public static TBuilder RequireClientKind<TBuilder>(this TBuilder builder, params ClientKind[] kinds)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(new AllowedClientKinds(kinds));

    /// <summary>
    /// Versioned resource (README §6.1): PATCH/PUT/DELETE need <c>If-Match</c> (428 <c>common.version.required</c>);
    /// the version is read with <see cref="ApiRequest.ExpectedVersion"/>; responses write <c>ETag</c> through
    /// <see cref="HttpResults.Versioned{T}"/>.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <returns>The builder.</returns>
    public static TBuilder Versioned<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(VersionedMarker.Instance);

    /// <summary>Honours <c>Idempotency-Key</c> (API §5.5).</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <returns>The builder.</returns>
    public static TBuilder Idempotent<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(IdempotentMarker.Instance);

    /// <summary>Binds <c>limit</c>, <c>cursor</c> and <c>sort</c> (read with <see cref="ApiRequest.Paging"/>).</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <param name="defaultLimit">The limit when none is given.</param>
    /// <returns>The builder.</returns>
    public static TBuilder WithPaging<TBuilder>(this TBuilder builder, int defaultLimit = 100)
        where TBuilder : IEndpointConventionBuilder =>
        builder.With(new PagingDefaults(defaultLimit));

    /// <summary>Applies the <c>heavy</c> rate limit policy (2 concurrent per session, queue 4).</summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The endpoint or group.</param>
    /// <returns>The builder.</returns>
    public static TBuilder Heavy<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireRateLimiting(HeavyPolicy);

    private static TBuilder With<TBuilder>(this TBuilder builder, object metadata)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint =>
        {
            endpoint.Metadata.Add(metadata);
            ApiEndpointFilter.Install(endpoint);
        });
        return builder;
    }
}

/// <summary>Metadata of <see cref="EndpointConventions.RequirePermission{TBuilder}"/>.</summary>
/// <param name="Permission">The permission.</param>
public sealed record PermissionRequirement(PermissionCode Permission);

/// <summary>Metadata of <see cref="EndpointConventions.RequireAuthentication{TBuilder}"/>.</summary>
public sealed class AuthenticationRequired
{
    /// <summary>Gets the shared instance.</summary>
    public static AuthenticationRequired Instance { get; } = new();

    private AuthenticationRequired()
    {
    }
}

/// <summary>Metadata of <see cref="EndpointConventions.ConfigurationEndpoint{TBuilder}"/>.</summary>
public sealed class ConfigurationEndpointMarker
{
    /// <summary>Gets the shared instance.</summary>
    public static ConfigurationEndpointMarker Instance { get; } = new();

    private ConfigurationEndpointMarker()
    {
    }
}

/// <summary>Metadata of <see cref="EndpointConventions.RequireClientKind{TBuilder}"/>.</summary>
/// <param name="Kinds">The allowed kinds.</param>
public sealed record AllowedClientKinds(IReadOnlyList<ClientKind> Kinds);

/// <summary>Metadata of <see cref="EndpointConventions.Versioned{TBuilder}"/>.</summary>
public sealed class VersionedMarker
{
    /// <summary>Gets the shared instance.</summary>
    public static VersionedMarker Instance { get; } = new();

    private VersionedMarker()
    {
    }
}

/// <summary>Metadata of <see cref="EndpointConventions.Idempotent{TBuilder}"/>.</summary>
public sealed class IdempotentMarker
{
    /// <summary>Gets the shared instance.</summary>
    public static IdempotentMarker Instance { get; } = new();

    private IdempotentMarker()
    {
    }
}

/// <summary>Metadata of <see cref="EndpointConventions.WithPaging{TBuilder}"/>.</summary>
/// <param name="DefaultLimit">The limit when none is given.</param>
public sealed record PagingDefaults(int DefaultLimit);
