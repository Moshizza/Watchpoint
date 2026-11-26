using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Text;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Api.Internal.System;

/// <summary>System and job endpoints owned by the API (spec API §6).</summary>
internal static class SystemEndpoints
{
    public const string ApiVersion = "1.0";

    public const int LiveProtocol = 1;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var system = endpoints.MapGroup("/api/v1/system").WithTags("System");
        system.MapGet("/info", Info).AllowAnonymous().WithName("System_GetInfo").WithSummary("Product, version and node facts");
        system.MapGet("/ca", Ca).AllowAnonymous().WithName("System_GetCa").WithSummary("Site CA certificate (PEM) and SHA-256 fingerprint");
        system.MapGet("/nodes", Nodes).RequireAuthentication().WithName("System_ListNodes").WithSummary("Nodes of the site for client failover");

        var jobs = endpoints.MapGroup("/api/v1/jobs").WithTags("Jobs");
        jobs.MapGet("/{id:guid}", GetJob).RequireAuthentication().WithName("Jobs_GetJob").WithSummary("Job status, progress and result");
        jobs.MapDelete("/{id:guid}", CancelJob).RequireAuthentication().WithName("Jobs_CancelJob").WithSummary("Cancel a running job");
    }

    private static IResult Info(SystemInfo info, IServiceProvider services, TimeProvider time) => Results.Json(new SystemInfoDto(
        "Watchpoint", info.Version, ApiVersion, LiveProtocol, services.GetService<ISiteNameProvider>()?.GetSiteName(),
        info.NodeId, info.NodeName, info.Environment, info.Features, SupportedCultures.All, time.GetUtcNow()), ApiJson.Options);

    private static IResult Ca(IServiceProvider services)
    {
        if (services.GetService<ISiteCertificateService>() is not { } certificates)
        {
            return HttpResults.Problem(Kernel.Results.CommonErrors.Unavailable("certificates"));
        }

        using var ca = certificates.GetCaCertificate();
        return Results.Json(new CaDto(ca.ExportCertificatePem(), certificates.GetCaFingerprintSha256()), ApiJson.Options);
    }

    private static IResult Nodes(SystemInfo info) =>
        Results.Json(new[] { new NodeDto(info.NodeId, info.NodeName, info.Urls) }, ApiJson.Options);

    private static IResult GetJob(Guid id, HttpContext context, IJobRegistry jobs) =>
        Visible(jobs.Find(id), context.CurrentUser()) is { } job
            ? Results.Json(JobDto.Of(job), ApiJson.Options)
            : HttpResults.Problem(ApiErrors.JobNotFound(id.ToString()));

    private static IResult CancelJob(Guid id, HttpContext context, IJobRegistry jobs)
    {
        if (Visible(jobs.Find(id), context.CurrentUser()) is null)
        {
            return HttpResults.Problem(ApiErrors.JobNotFound(id.ToString()));
        }

        jobs.Cancel(id);
        return Results.Json(JobDto.Of(jobs.Find(id)!), ApiJson.Options);
    }

    private static JobInfo? Visible(JobInfo? job, ICurrentUser user) =>
        job is not null && (job.Owner == user.UserId || user.IsAdministrator) ? job : null;

    internal sealed record SystemInfoDto(string Product, string Version, string ApiVersion, int LiveProtocol, string? SiteName, Guid NodeId,
        string NodeName, string Environment, IReadOnlyList<string> Features, IReadOnlyList<string> SupportedCultures, DateTimeOffset Time);

    internal sealed record CaDto(string Pem, string FingerprintSha256);

    internal sealed record NodeDto(Guid Id, string Name, IReadOnlyList<string> Urls);
}

/// <summary>JSON shape of a job (spec API §5.6).</summary>
internal sealed record JobDto(Guid JobId, string Kind, string Status, int Progress, string? ProgressMessageKey, JsonElement? Result,
    JobErrorDto? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public static JobDto Of(JobInfo job) => new(job.Id, job.Kind, JsonNamingPolicy.CamelCase.ConvertName(job.Status.ToString()), job.Progress,
        job.ProgressMessageKey, job.Result, job.Error is { } e ? new JobErrorDto(e.Code.Value, e.MessageKey) : null, job.StartedAt, job.FinishedAt);
}

/// <summary>The error of a failed job.</summary>
internal sealed record JobErrorDto(string Code, string MessageKey);
