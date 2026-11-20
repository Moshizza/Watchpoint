using System.Globalization;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Internal.Context;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Integrations.Contracts;

/// <summary>
/// The connection endpoints (spec IRT §10) and the connectors' inbound HTTP route. Until the API framework and its
/// permission filter (T1.19), the endpoints that change the node are mapped only in Development and answer only
/// loopback requests; the <c>GET</c> endpoints are always mapped.
/// </summary>
public static class ConnectionEndpoints
{
    /// <summary>Maps <c>/api/v1/connections</c> and <c>/integrations/{connectionId}/{**path}</c>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/>.</returns>
    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IntegrationsOptions>();

        // Inbound connector routes: authenticated by the connector's declared scheme before its handler runs.
        endpoints.Map(InboundRouter.RoutePrefix + "/{connectionId:guid}/{**path}", (HttpContext http, Guid connectionId, string? path, InboundRouter router) =>
            router.HandleAsync(http, connectionId, path));

        var group = endpoints.MapGroup("/api/v1/connections");
        group.MapGet(string.Empty, (IConnectionManager manager) => EndpointResults.Json(StatusCodes.Status200OK, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var connection in manager.List())
            {
                Write(writer, connection, manager.GetStatus(connection.Id));
            }

            writer.WriteEndArray();
            writer.WriteNull("nextCursor");
            writer.WriteEndObject();
        }));
        group.MapGet("{id:guid}", (Guid id, IConnectionManager manager) =>
            manager.Get(ConnectionId.From(id)).TryGetValue(out var connection, out var error)
                ? EndpointResults.Json(StatusCodes.Status200OK, writer => Write(writer, connection, manager.GetStatus(connection.Id)))
                : EndpointResults.Problem(error));
        group.MapGet("{id:guid}/events", async (Guid id, string? from, string? to, IConnectionManager manager, TimeProvider time, CancellationToken ct) =>
        {
            var until = Time(to) ?? time.GetUtcNow().AddSeconds(1);
            var since = Time(from) ?? until.AddDays(-1);
            if (!manager.Get(ConnectionId.From(id)).IsSuccess)
            {
                return EndpointResults.Problem(IntegrationErrors.ConnectionNotFoundError(ConnectionId.From(id)));
            }

            var events = await manager.GetEventsAsync(ConnectionId.From(id), since, until, ct).ConfigureAwait(false);
            return EndpointResults.Json(StatusCodes.Status200OK, writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("items");
                foreach (var e in events)
                {
                    writer.WriteStartObject();
                    writer.WriteString("at", e.At.UtcDateTime);
                    writer.WriteString("state", ConnectionColumns.StateText(e.State));
                    WriteMessage(writer, "reason", e.Reason);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        });

        if (!options.IsDevelopment)
        {
            return endpoints;
        }

        var changes = endpoints.MapGroup("/api/v1/connections").AddEndpointFilter(EndpointResults.LoopbackOnly);
        changes.MapPost(string.Empty, CreateAsync);
        changes.MapPatch("{id:guid}", UpdateAsync);
        changes.MapDelete("{id:guid}", async (Guid id, bool? deleteDevices, IConnectionManager manager, CancellationToken ct) =>
            Done(await manager.DeleteAsync(ConnectionId.From(id), deleteDevices ?? false, Context(), ct).ConfigureAwait(false), StatusCodes.Status204NoContent));
        changes.MapPost("{id:guid}/start", async (Guid id, IConnectionManager manager, CancellationToken ct) =>
            Status(manager, id, await manager.StartAsync(ConnectionId.From(id), ct).ConfigureAwait(false)));
        changes.MapPost("{id:guid}/stop", async (Guid id, IConnectionManager manager, CancellationToken ct) =>
            Status(manager, id, await manager.StopAsync(ConnectionId.From(id), ct).ConfigureAwait(false)));
        changes.MapPost("{id:guid}/restart", async (Guid id, IConnectionManager manager, CancellationToken ct) =>
            Status(manager, id, await manager.RestartAsync(ConnectionId.From(id), ct).ConfigureAwait(false)));
        changes.MapPost("test", TestAsync);
        changes.MapPost("{id:guid}/discover", async (Guid id, IConnectionManager manager, CancellationToken ct) =>
        {
            // M1: the discovery runs within the request (the job system with progress on the live topic comes with the API).
            var discovered = await manager.DiscoverAsync(ConnectionId.From(id), null, ct).ConfigureAwait(false);
            return discovered.TryGetValue(out var snapshot, out var error)
                ? EndpointResults.Json(StatusCodes.Status200OK, writer => WriteInventory(writer, snapshot))
                : EndpointResults.Problem(error);
        });
        changes.MapPost("{id:guid}/rediscover", async (Guid id, IConnectionManager manager, CancellationToken ct) =>
        {
            var reconciled = await manager.DescribeAndReconcileAsync(ConnectionId.From(id), ct).ConfigureAwait(false);
            return reconciled.TryGetValue(out var result, out var error)
                ? EndpointResults.Json(StatusCodes.Status200OK, writer => WriteReconciliation(writer, result))
                : EndpointResults.Problem(error);
        });
        changes.MapPost("{id:guid}/protocol-trace", async (Guid id, HttpRequest request, IConnectionManager manager, CancellationToken ct) =>
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
            if (!body.RootElement.TryGetProperty("duration", out var duration) || Duration(duration.GetString()) is not { } value)
            {
                return EndpointResults.Problem(CommonErrors.ValidationFailed([CommonErrors.InvalidFormatAt("/duration", "ISO 8601 duration")]));
            }

            return Status(manager, id, await manager.SetProtocolTraceAsync(ConnectionId.From(id), value, ct).ConfigureAwait(false));
        });
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(HttpRequest request, IConnectionManager manager, CancellationToken ct)
    {
        using var body = await ReadAsync(request, ct).ConfigureAwait(false);
        if (body is null)
        {
            return InvalidBody();
        }

        var root = body.RootElement;
        var fields = new List<FieldError>();
        var name = Name(root, fields);
        var package = PackageId.Parse(String(root, "packageId")).TryGetValue(out var p, out _) ? p : default;
        if (package == default)
        {
            fields.Add(CommonErrors.RequiredAt("/packageId"));
        }

        SemVersion? version = null;
        if (String(root, "packageVersion") is { } text && !SemVersion.TryParse(text, out version))
        {
            fields.Add(CommonErrors.InvalidFormatAt("/packageVersion", "semver"));
        }

        var location = Guid.TryParse(String(root, "locationId"), out var l) && l != Guid.Empty ? LocationId.From(l) : default;
        if (location == default)
        {
            fields.Add(CommonErrors.RequiredAt("/locationId"));
        }

        var hostMode = HostModeOf(root, fields) ?? HostMode.InProcess;
        if (String(root, "serverType") is not { } serverType)
        {
            fields.Add(CommonErrors.RequiredAt("/serverType"));
            serverType = string.Empty;
        }

        if (fields.Count > 0)
        {
            return EndpointResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var created = await manager.CreateAsync(new NewConnection(name!, package, version, serverType, Settings(root), Secrets(root), location,
            Bool(root, "autoImport") ?? false, Bool(root, "startNow") ?? true) { HostMode = hostMode }, Context(), ct).ConfigureAwait(false);
        if (!created.TryGetValue(out var id, out var error))
        {
            return EndpointResults.Problem(error);
        }

        var connection = manager.Get(id).Value;
        return EndpointResults.Json(StatusCodes.Status201Created, writer => Write(writer, connection, manager.GetStatus(id)), $"/api/v1/connections/{id.Value:D}");
    }

    private static async Task<IResult> UpdateAsync(Guid id, HttpRequest request, IConnectionManager manager, CancellationToken ct)
    {
        var connectionId = ConnectionId.From(id);
        if (!manager.Get(connectionId).TryGetValue(out var current, out var error))
        {
            return EndpointResults.Problem(error);
        }

        var expected = current.Version;
        if (request.Headers.IfMatch.Count > 0 && !long.TryParse(request.Headers.IfMatch[0]?.Trim('"', 'W', '/'), NumberStyles.None, CultureInfo.InvariantCulture, out expected))
        {
            return EndpointResults.Problem(CommonErrors.ValidationFailed([CommonErrors.InvalidFormatAt("If-Match", "\"<version>\"")]));
        }

        using var body = await ReadAsync(request, ct).ConfigureAwait(false);
        if (body is null)
        {
            return InvalidBody();
        }

        var root = body.RootElement;
        var fields = new List<FieldError>();
        var name = root.TryGetProperty("name", out _) ? Name(root, fields) : null;
        var hostMode = HostModeOf(root, fields);
        LocationId? location = String(root, "locationId") is { } text ? Guid.TryParse(text, out var l) && l != Guid.Empty ? LocationId.From(l) : null : null;
        if (String(root, "locationId") is not null && location is null)
        {
            fields.Add(CommonErrors.InvalidFormatAt("/locationId", "uuid"));
        }

        TimeSpan? interval = null;
        if (String(root, "describeInterval") is { } d && (interval = Duration(d)) is null)
        {
            fields.Add(CommonErrors.InvalidFormatAt("/describeInterval", "ISO 8601 duration"));
        }

        if (fields.Count > 0)
        {
            return EndpointResults.Problem(CommonErrors.ValidationFailed(fields));
        }

        var update = new ConnectionUpdate(name, root.TryGetProperty("settings", out _) ? Settings(root) : null,
            root.TryGetProperty("secrets", out _) ? Secrets(root) : null, location, Bool(root, "enabled"), Bool(root, "autoImport"), hostMode, interval);
        var updated = await manager.UpdateAsync(connectionId, update, expected, Context(), ct).ConfigureAwait(false);
        return Status(manager, id, updated);
    }

    private static async Task<IResult> TestAsync(HttpRequest request, IConnectionManager manager, CancellationToken ct)
    {
        using var body = await ReadAsync(request, ct).ConfigureAwait(false);
        if (body is null)
        {
            return InvalidBody();
        }

        var root = body.RootElement;
        if (!PackageId.Parse(String(root, "packageId")).TryGetValue(out var package, out _) || String(root, "serverType") is not { } serverType)
        {
            return EndpointResults.Problem(CommonErrors.ValidationFailed([CommonErrors.RequiredAt("/packageId"), CommonErrors.RequiredAt("/serverType")]));
        }

        var version = SemVersion.TryParse(String(root, "packageVersion"), out var parsed) ? parsed : null;
        ConnectionId? existing = Guid.TryParse(String(root, "connectionId"), out var c) && c != Guid.Empty ? ConnectionId.From(c) : null;
        var result = await manager.TestAsync(new TestConnectionRequest(package, version, serverType, Settings(root), Secrets(root), existing), ct).ConfigureAwait(false);
        return EndpointResults.Json(StatusCodes.Status200OK, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", result.Success);
            if (result.Failure is { } failure)
            {
                writer.WriteString("failure", JsonNamingPolicy.CamelCase.ConvertName(failure.ToString()));
            }
            else
            {
                writer.WriteNull("failure");
            }

            WriteMessage(writer, "message", result.Message?.ToMessage(package.Value));
            writer.WriteEndObject();
        });
    }

    private static ChangeContext Context() => new(null, "api");

    private static IResult InvalidBody() =>
        EndpointResults.Problem(CommonErrors.ValidationFailed([CommonErrors.InvalidFormatAt(string.Empty, "JSON object")]));

    private static IResult Done(Result result, int status) => result.IsSuccess ? Results.StatusCode(status) : EndpointResults.Problem(result.Error);

    private static IResult Status(IConnectionManager manager, Guid id, Result result)
    {
        if (!result.IsSuccess)
        {
            return EndpointResults.Problem(result.Error);
        }

        var connectionId = ConnectionId.From(id);
        return manager.Get(connectionId).TryGetValue(out var connection, out var error)
            ? EndpointResults.Json(StatusCodes.Status200OK, writer => Write(writer, connection, manager.GetStatus(connectionId)))
            : EndpointResults.Problem(error);
    }

    private static async Task<JsonDocument?> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static JsonElement Settings(JsonElement root) =>
        root.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object ? settings.Clone() : JsonDocument.Parse("{}").RootElement.Clone();

    private static Dictionary<string, string> Secrets(JsonElement root) =>
        root.TryGetProperty("secrets", out var secrets) && secrets.ValueKind == JsonValueKind.Object
            ? secrets.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : [];

    private static LocalizedName? Name(JsonElement root, List<FieldError> fields)
    {
        if (root.TryGetProperty("name", out var name))
        {
            if (name.ValueKind == JsonValueKind.String)
            {
                return LocalizedName.Of(name.GetString()!);
            }

            if (name.ValueKind == JsonValueKind.Object && name.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var translations = name.TryGetProperty("translations", out var t) && t.ValueKind == JsonValueKind.Object
                    ? t.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
                    : [];
                return new LocalizedName(d.GetString()!, translations);
            }
        }

        fields.Add(CommonErrors.RequiredAt("/name"));
        return null;
    }

    private static HostMode? HostModeOf(JsonElement root, List<FieldError> fields)
    {
        if (String(root, "hostMode") is not { } text)
        {
            return null;
        }

        HostMode? mode = text switch
        {
            "inProcess" => HostMode.InProcess,
            "isolated" => HostMode.Isolated,
            "remote" => HostMode.Remote,
            _ => null,
        };
        if (mode is null)
        {
            fields.Add(CommonErrors.NotAllowedAt("/hostMode", ["inProcess", "isolated", "remote"]));
        }

        return mode;
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Write(Utf8JsonWriter writer, Connection connection, ConnectionStatus status)
    {
        writer.WriteStartObject();
        writer.WriteString("id", connection.Id.Value.ToString("D"));
        writer.WriteStartObject("name");
        writer.WriteString("default", connection.Name.Default);
        writer.WriteStartObject("translations");
        foreach (var (culture, text) in connection.Name.Translations)
        {
            writer.WriteString(culture, text);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteString("packageId", connection.PackageId.Value);
        writer.WriteString("packageVersion", connection.PackageVersion.ToString());
        writer.WriteString("serverType", connection.ServerType);
        writer.WritePropertyName("settings");
        connection.Settings.WriteTo(writer);
        writer.WriteStartObject("secrets");
        foreach (var field in connection.Secrets.Keys.Order(StringComparer.Ordinal))
        {
            // Never a value, never a secret id: only that the field is set.
            writer.WriteString(field, "set");
        }

        writer.WriteEndObject();
        writer.WriteString("locationId", connection.LocationId.Value.ToString("D"));
        writer.WriteString("hostMode", ConnectionColumns.HostModeText(connection.HostMode));
        writer.WriteBoolean("enabled", connection.Enabled);
        writer.WriteBoolean("autoImport", connection.AutoImport);
        writer.WriteString("describeInterval", XmlConvert.ToString(connection.DescribeInterval));
        if (connection.ProtocolTraceUntil is { } trace)
        {
            writer.WriteString("protocolTraceUntil", trace.UtcDateTime);
        }
        else
        {
            writer.WriteNull("protocolTraceUntil");
        }

        writer.WriteNumber("version", connection.Version);
        writer.WriteStartObject("status");
        writer.WriteString("state", ConnectionColumns.StateText(status.State));
        WriteMessage(writer, "reason", status.Reason);
        writer.WriteString("since", status.Since.UtcDateTime);
        writer.WriteNumber("restartCount", status.RestartCount);
        if (status.LastConnectedAt is { } connected)
        {
            writer.WriteString("lastConnectedAt", connected.UtcDateTime);
        }
        else
        {
            writer.WriteNull("lastConnectedAt");
        }

        writer.WriteStartObject("counters");
        writer.WriteNumber("observationsAccepted", status.Counters.ObservationsAccepted);
        writer.WriteNumber("observationsRejected", status.Counters.ObservationsRejected);
        writer.WriteNumber("unknownDevices", status.Counters.UnknownDevices);
        writer.WriteNumber("commandsSucceeded", status.Counters.CommandsSucceeded);
        writer.WriteNumber("commandsFailed", status.Counters.CommandsFailed);
        writer.WriteNumber("commandLatencyP95Ms", status.Counters.CommandLatencyP95Ms);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteMessage(Utf8JsonWriter writer, string property, Message? message)
    {
        if (message is null)
        {
            writer.WriteNull(property);
            return;
        }

        writer.WriteStartObject(property);
        writer.WriteString("key", message.Key);
        writer.WriteStartObject("args");
        foreach (var (name, value) in message.Args)
        {
            writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteInventory(Utf8JsonWriter writer, InventorySnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("isComplete", snapshot.IsComplete);
        writer.WriteStartArray("servers");
        foreach (var server in snapshot.Servers)
        {
            writer.WriteStartObject();
            writer.WriteString("nativeId", server.NativeId);
            writer.WriteString("typeId", server.TypeId);
            writer.WriteString("typeVersion", server.TypeVersion);
            writer.WriteString("vendorName", server.VendorName);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("devices");
        foreach (var device in snapshot.Devices)
        {
            writer.WriteStartObject();
            writer.WriteString("nativeId", device.NativeId);
            writer.WriteString("serverNativeId", device.ServerNativeId);
            writer.WriteString("typeId", device.TypeId);
            writer.WriteString("typeVersion", device.TypeVersion);
            writer.WriteString("vendorName", device.VendorName);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteReconciliation(Utf8JsonWriter writer, ReconciliationResult result)
    {
        writer.WriteStartObject();
        writer.WriteNumber("unchanged", result.Unchanged);
        writer.WriteNumber("updated", result.Updated);
        writer.WriteNumber("orphaned", result.Orphaned);
        writer.WriteNumber("restored", result.Restored);
        writer.WriteNumber("newDiscovered", result.NewDiscovered);
        writer.WriteNumber("imported", result.Imported);
        writer.WriteStartArray("warnings");
        foreach (var warning in result.Warnings)
        {
            writer.WriteStartObject();
            writer.WriteString("code", warning.Code);
            writer.WriteString("nativeId", warning.NativeId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
