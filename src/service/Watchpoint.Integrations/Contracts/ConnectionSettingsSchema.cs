using System.Text.Json;

namespace Watchpoint.Integrations.Contracts;

/// <summary>What a client needs to edit the settings of a connection (spec IRT §4.2, §10).</summary>
/// <param name="ServerType">The connector's server type.</param>
/// <param name="Settings">The settings schema (parameter schema, SCM §5.4).</param>
/// <param name="Secrets">The secret fields.</param>
/// <param name="UiHints">The schema's <c>x-wp-ui</c> extension, when present.</param>
public sealed record ConnectionSettingsSchema(string ServerType, JsonElement Settings,
    IReadOnlyList<SecretFieldSchema> Secrets, JsonElement? UiHints);

/// <summary>A secret of a connection.</summary>
/// <param name="Id">The field id.</param>
/// <param name="Kind">The secret kind (<c>password</c>, <c>token</c>…).</param>
/// <param name="Required">Whether a connection cannot be created without it.</param>
public sealed record SecretFieldSchema(string Id, string Kind, bool Required);
