namespace Watchpoint.Api;

/// <summary>Settings of the REST framework (spec API §9): sections <c>Api</c> and <c>Http:TrustedProxies</c>.</summary>
public sealed class ApiOptions
{
    /// <summary>Gets or sets the default request body limit in bytes (<c>Api:MaxBodyBytes</c>, 10 MB).</summary>
    public long MaxBodyBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Gets or sets the trusted proxies, CIDR (<c>Http:TrustedProxies</c>); empty: forwarded headers ignored.</summary>
    public IList<string> TrustedProxies { get; set; } = [];

    /// <summary>Gets or sets the rate limits (<c>Api:RateLimits:*</c>, API §4.2).</summary>
    public ApiRateLimits RateLimits { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the node runs in Production (HSTS, HTTPS redirection; OpenAPI off).</summary>
    public bool IsProduction { get; set; } = true;
}

/// <summary>Rate limits (API §4.2).</summary>
public sealed class ApiRateLimits
{
    /// <summary>Gets or sets the anonymous rate per remote IP (requests per second).</summary>
    public int AnonymousPerSecond { get; set; } = 10;

    /// <summary>Gets or sets the anonymous burst.</summary>
    public int AnonymousBurst { get; set; } = 20;

    /// <summary>Gets or sets the authenticated rate per session (requests per second).</summary>
    public int AuthenticatedPerSecond { get; set; } = 50;

    /// <summary>Gets or sets the authenticated burst.</summary>
    public int AuthenticatedBurst { get; set; } = 100;

    /// <summary>Gets or sets the concurrent heavy requests per session.</summary>
    public int HeavyConcurrency { get; set; } = 2;

    /// <summary>Gets or sets the queued heavy requests per session.</summary>
    public int HeavyQueue { get; set; } = 4;
}
