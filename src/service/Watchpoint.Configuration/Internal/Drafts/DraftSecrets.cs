using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Configuration.Internal.Drafts;

/// <summary>
/// Secrets in drafts (spec CFG §6.3): a field <c>{ "$secret": "value" }</c> of an entry is stored in the vault (owner
/// <c>Configuration</c>, entity = the draft) and replaced by <c>{ "$secretRef": "&lt;id&gt;" }</c>, so the value is never kept in the draft.
/// </summary>
internal sealed class DraftSecrets(ISecretVault vault)
{
    public const string Module = "Configuration";

    private static SecretOwner OwnerOf(DraftId draft) => new(Module, draft.Value);

    /// <summary>Replaces the <c>$secret</c> values of a document by references, storing them in the vault.</summary>
    /// <returns>The new content and the secrets created (to delete when the change does not go through).</returns>
    public async Task<Result<(JsonElement Content, IReadOnlyList<DraftSecretRef> Created)>> StoreAsync(DraftId draft, Guid entryId, JsonElement content, CancellationToken ct)
    {
        var root = JsonNode.Parse(content.GetRawText());
        List<DraftSecretRef> created = [];
        var failure = await WalkAsync(root, string.Empty, draft, entryId, created, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            await DeleteAsync(draft, created, ct).ConfigureAwait(false);
            return failure;
        }

        return (JsonSerializer.SerializeToElement(root), created);
    }

    /// <summary>The secret references a document holds.</summary>
    public static IReadOnlySet<SecretId> ReferencesIn(JsonElement content)
    {
        HashSet<SecretId> ids = [];
        Collect(content, ids);
        return ids;
    }

    public async Task DeleteAsync(DraftId draft, IEnumerable<DraftSecretRef> secrets, CancellationToken ct)
    {
        foreach (var secret in secrets)
        {
            await vault.DeleteAsync(secret.Secret, OwnerOf(draft), ct).ConfigureAwait(false);
        }
    }

    private static void Collect(JsonElement element, HashSet<SecretId> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "$secretRef" && property.Value.ValueKind == JsonValueKind.String && SecretId.TryParse(property.Value.GetString(), out var id))
                    {
                        ids.Add(id);
                    }
                    else
                    {
                        Collect(property.Value, ids);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, ids);
                }

                break;
        }
    }

    private async Task<Error?> WalkAsync(JsonNode? node, string path, DraftId draft, Guid entryId, List<DraftSecretRef> created, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count == 1 && obj.ContainsKey("$secret"):
                {
                    if (obj["$secret"] is not JsonValue value || !value.TryGetValue<string>(out var text))
                    {
                        return Error.Create(ErrorKind.Validation, ConfigurationErrors.SecretInvalid, ("path", path));
                    }

                    var name = $"draft/{draft.Value:D}/{entryId:D}{path}";
                    var result = await vault.CreateAsync(name, SecretKind.Password, OwnerOf(draft), Encoding.UTF8.GetBytes(text), null, ct).ConfigureAwait(false);
                    if (!result.TryGetValue(out var id, out var error))
                    {
                        return error;
                    }

                    created.Add(new DraftSecretRef(entryId, path, id));
                    obj.Clear();
                    obj["$secretRef"] = id.ToString();
                    return null;
                }

            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (await WalkAsync(child, path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal), draft, entryId, created, ct).ConfigureAwait(false) is { } failure)
                    {
                        return failure;
                    }
                }

                return null;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (await WalkAsync(array[i], path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), draft, entryId, created, ct).ConfigureAwait(false) is { } failure)
                    {
                        return failure;
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
