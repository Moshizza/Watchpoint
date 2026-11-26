using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Tests.Support;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Api.Tests;

public sealed class ProblemTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task<JsonElement> ProblemOf(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement.Clone();
        root.GetProperty("code").GetString().ShouldBe(code);
        return root;
    }

    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.Unauthenticated, 401)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.PreconditionFailed, 412)]
    [InlineData(ErrorKind.Unprocessable, 422)]
    [InlineData(ErrorKind.TooManyRequests, 429)]
    [InlineData(ErrorKind.Unavailable, 503)]
    [InlineData(ErrorKind.Internal, 500)]
    public void API_U_001_Result_mapping_for_every_ErrorKind(ErrorKind kind, int status)
    {
        HttpResults.StatusOf(Error.Create(kind, "test.some.error")).ShouldBe(status);
        HttpResults.StatusOf(Error.Conflict("common.version.conflict")).ShouldBe(412);
        HttpResults.StatusOf(ApiErrors.VersionRequired()).ShouldBe(428);
        HttpResults.StatusOf(ApiErrors.RequestTooLarge(1)).ShouldBe(413);
    }

    [Fact]
    public async Task API_U_002_Problem_details_shape_without_developer_detail()
    {
        var fields = new[] { FieldError.Create("/settings/port", CommonErrorCodes.ValueOutOfRange, ("min", 1L), ("max", 65535L)) };
        var error = CommonErrors.ValidationFailed(fields) with { DeveloperDetail = "SECRET-DETAIL" };
        var internalError = Error.Internal("SECRET-INTERNAL");
        await using var host = await ApiHost.StartAsync(app =>
        {
            app.MapGet("/api/v1/tests/invalid", () => HttpResults.Problem(error)).AllowAnonymous().WithName("Tests_GetInvalid");
            app.MapGet("/api/v1/tests/internal", () => HttpResults.Problem(internalError)).AllowAnonymous().WithName("Tests_GetInternal");
        });

        var response = await host.Client.GetAsync("/api/v1/tests/invalid", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var problem = await ProblemOf(response, HttpStatusCode.BadRequest, "common.validation.failed");
        problem.GetProperty("type").GetString().ShouldBe("https://watchpoint.dev/errors/common.validation.failed");
        problem.GetProperty("title").GetString().ShouldBe("common.validation.failed");
        problem.GetProperty("status").GetInt32().ShouldBe(400);
        problem.GetProperty("messageKey").GetString().ShouldBe("errors.common.validation.failed");
        problem.GetProperty("args").ValueKind.ShouldBe(JsonValueKind.Object);
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        var field = problem.GetProperty("errors")[0];
        field.GetProperty("path").GetString().ShouldBe("/settings/port");
        field.GetProperty("code").GetString().ShouldBe("common.value.out_of_range");
        field.GetProperty("args").GetProperty("max").GetInt64().ShouldBe(65535);
        body.ShouldNotContain("SECRET");

        var internalBody = await (await host.Client.GetAsync("/api/v1/tests/internal", Ct)).Content.ReadAsStringAsync(Ct);
        internalBody.ShouldNotContain("SECRET");
    }

    [Fact]
    public async Task API_U_003_Unhandled_exception_answers_500_and_is_logged_with_the_trace_id()
    {
        await using var host = await ApiHost.StartAsync(app =>
            app.MapGet("/api/v1/tests/boom", IResult () => throw new InvalidOperationException("SECRET-BOOM")).AllowAnonymous().WithName("Tests_GetBoom"));

        var response = await host.Client.GetAsync("/api/v1/tests/boom", Ct);

        var problem = await ProblemOf(response, HttpStatusCode.InternalServerError, "common.internal");
        var traceId = problem.GetProperty("traceId").GetString();
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("SECRET");
        var logged = host.Logs.WithEventId(2001).ShouldHaveSingleItem();
        logged.Properties["TraceId"].ShouldBe(traceId);
        logged.Properties["Route"].ShouldBe("/api/v1/tests/boom");
        logged.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task API_U_004_Request_log_has_the_route_template_never_the_token_query_or_body()
    {
        await using var host = await ApiHost.StartAsync(app =>
            app.MapPost("/api/v1/tests/{id}", (string id) => Results.Ok()).AllowAnonymous().WithName("Tests_PostThing"));

        using var content = new StringContent("{\"password\":\"SECRET-BODY\"}", System.Text.Encoding.UTF8, "application/json");
        await host.Client.PostAsync("/api/v1/tests/SECRET-ID?access_token=SECRET-TOKEN", content, Ct);

        var line = host.Logs.WithEventId(2002).ShouldHaveSingleItem();
        line.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Information);
        line.Properties["Route"].ShouldBe("/api/v1/tests/{id}");
        line.Properties["Status"].ShouldBe(200);
        host.Logs.Entries.ShouldAllBe(e => !e.RenderedMessage.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task API_U_020_Body_size_limit_answers_413()
    {
        await using var host = await ApiHost.StartAsync(
            app => app.MapPost("/api/v1/tests/body", (JsonElement body) => Results.Ok()).AllowAnonymous().WithName("Tests_PostBody"),
            new Dictionary<string, string?> { ["Api:MaxBodyBytes"] = "100" });

        using var content = new StringContent("{\"a\":\"" + new string('x', 200) + "\"}", System.Text.Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync("/api/v1/tests/body", content, Ct);

        var problem = await ProblemOf(response, HttpStatusCode.RequestEntityTooLarge, "api.request.too_large");
        problem.GetProperty("args").GetProperty("max").GetInt64().ShouldBe(100);
    }

    [Fact]
    public async Task API_U_021_Json_depth_33_answers_400()
    {
        await using var host = await ApiHost.StartAsync(app =>
            app.MapPost("/api/v1/tests/body", (JsonElement body) => Results.Ok()).AllowAnonymous().WithName("Tests_PostBody"));

        static string Nested(int depth) => string.Concat(Enumerable.Repeat("[", depth)) + string.Concat(Enumerable.Repeat("]", depth));
        using var ok = new StringContent(Nested(32), System.Text.Encoding.UTF8, "application/json");
        (await host.Client.PostAsync("/api/v1/tests/body", ok, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var deep = new StringContent(Nested(33), System.Text.Encoding.UTF8, "application/json");
        await ProblemOf(await host.Client.PostAsync("/api/v1/tests/body", deep, Ct), HttpStatusCode.BadRequest, "api.request.invalid");
    }
}
