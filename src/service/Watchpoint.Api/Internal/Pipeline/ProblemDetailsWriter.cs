using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Internal.Pipeline;

/// <summary>Writes RFC 9457 problem details (spec API §5.4, README §6.2). <see cref="Error.DeveloperDetail"/> is never written.</summary>
internal static class ProblemDetailsWriter
{
    public const string ContentType = "application/problem+json";

    public static string TraceIdOf(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;

    public static async Task WriteAsync(HttpContext context, Error error, int? status = null)
    {
        var code = status ?? HttpResults.StatusOf(error);
        context.Response.StatusCode = code;
        context.Response.ContentType = ContentType;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, error, code, TraceIdOf(context));
        }

        context.Response.ContentLength = buffer.WrittenCount;
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    public static void Write(Utf8JsonWriter writer, Error error, int status, string traceId)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "https://watchpoint.dev/errors/" + error.Code.Value);
        writer.WriteString("title", error.Code.Value);
        writer.WriteNumber("status", status);
        writer.WriteString("code", error.Code.Value);
        writer.WriteString("messageKey", error.MessageKey);
        writer.WritePropertyName("args");
        WriteArgs(writer, error.Kind == ErrorKind.Internal ? new Dictionary<string, object?>() : error.Args);
        if (error.FieldErrors is { Count: > 0 } fields)
        {
            writer.WriteStartArray("errors");
            foreach (var field in fields)
            {
                writer.WriteStartObject();
                writer.WriteString("path", field.Path);
                writer.WriteString("code", field.Code.Value);
                writer.WritePropertyName("args");
                WriteArgs(writer, field.Args);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteString("traceId", traceId);
        writer.WriteEndObject();
    }

    private static void WriteArgs(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> args)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in args)
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object), ApiJson.Options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>An <see cref="IResult"/> writing problem details.</summary>
internal sealed class ProblemResult(Error error) : IResult, IStatusCodeHttpResult
{
    public Error Error { get; } = error;

    public int? StatusCode => HttpResults.StatusOf(Error);

    public Task ExecuteAsync(HttpContext httpContext) => ProblemDetailsWriter.WriteAsync(httpContext, Error);
}
