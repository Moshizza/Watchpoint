using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>
/// <c>&lt;Data&gt;/secrets/bootstrap.json</c> (spec SEC §6.3): items sealed with the master key (AAD <c>wp-bootstrap|name</c>),
/// written atomically (temp file, flush, rename).
/// </summary>
internal sealed class BootstrapSecretStore(SecretsOptions options, MasterKeyAccessor keys, ILogger<BootstrapSecretStore> logger) : IBootstrapSecretStore
{
    private readonly Lock _lock = new();

    public string FilePath => Path.Join(options.SecretsDirectory, "bootstrap.json");

    /// <summary>Test hook (SEC-U-016): runs after the temp file is written, before the rename.</summary>
    internal Action? AfterTempWrite { get; set; }

    public Result<SecretValue> Read(string name)
    {
        lock (_lock)
        {
            var items = Load();
            if (!items.TryGetValue(name, out var item))
            {
                return SecretsErrors.NotFound("bootstrap:" + name);
            }

            var key = keys.Ring.Find(item.KeyId);
            var value = key is null ? null : EnvelopeCipher.OpenWithMasterKey(item.Nonce, item.Ciphertext, Aad(name), key);
            if (value is null)
            {
                SecretsLog.IntegrityFailed(logger, "bootstrap:" + name);
                return SecretsErrors.IntegrityFailed("bootstrap:" + name);
            }

            return value;
        }
    }

    public Result Write(string name, ReadOnlySpan<byte> value)
    {
        if (!SecretRef.IsBootstrapName(name))
        {
            return SecretsErrors.RefInvalid();
        }

        lock (_lock)
        {
            var items = Load();
            var key = keys.Ring.Current;
            var (nonce, ciphertext) = EnvelopeCipher.SealWithMasterKey(value, Aad(name), key);
            items[name] = new Item(key.KeyId, nonce, ciphertext);
            Save(items);
            return Result.Success();
        }
    }

    public bool Exists(string name)
    {
        lock (_lock)
        {
            return Load().ContainsKey(name);
        }
    }

    /// <summary>Re-seals every item with the current master key; returns the number of items rewritten.</summary>
    public int RewrapAll()
    {
        lock (_lock)
        {
            var items = Load();
            var count = 0;
            foreach (var name in items.Keys.ToList())
            {
                var item = items[name];
                if (item.KeyId == keys.Ring.Current.KeyId || keys.Ring.Find(item.KeyId) is not { } old)
                {
                    continue;
                }

                using var value = EnvelopeCipher.OpenWithMasterKey(item.Nonce, item.Ciphertext, Aad(name), old)
                    ?? throw new SecretsException(SecretsErrors.IntegrityFailed("bootstrap:" + name));
                var (nonce, ciphertext) = value.Use(v => EnvelopeCipher.SealWithMasterKey(v, Aad(name), keys.Ring.Current));
                items[name] = new Item(keys.Ring.Current.KeyId, nonce, ciphertext);
                count++;
            }

            if (count > 0)
            {
                Save(items);
            }

            return count;
        }
    }

    /// <summary>Imports <c>&lt;Data&gt;/nats/token</c> (HST §7.4) as <c>nats.token</c> and deletes the file.</summary>
    public void ImportLegacyNatsToken()
    {
        var legacy = Path.Join(options.DataDirectory, "nats", "token");
        if (!File.Exists(legacy))
        {
            return;
        }

        var token = File.ReadAllBytes(legacy);
        try
        {
            var written = Write("nats.token", token);
            if (!written.IsSuccess)
            {
                throw new SecretsException(written.Error);
            }

            File.Delete(legacy);
            SecretsLog.NatsTokenImported(logger);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(token);
        }
    }

    private static byte[] Aad(string name) => Encoding.UTF8.GetBytes("wp-bootstrap|" + name);

    private Dictionary<string, Item> Load()
    {
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        if (!File.Exists(FilePath))
        {
            return items;
        }

        using var json = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        var root = json.RootElement;
        var fileKeyId = root.TryGetProperty("keyId", out var k) ? k.GetString() ?? string.Empty : string.Empty;
        foreach (var item in root.GetProperty("items").EnumerateObject())
        {
            var keyId = item.Value.TryGetProperty("keyId", out var ik) ? ik.GetString() ?? fileKeyId : fileKeyId;
            items[item.Name] = new Item(keyId, Convert.FromBase64String(item.Value.GetProperty("nonce").GetString()!),
                Convert.FromBase64String(item.Value.GetProperty("ciphertext").GetString()!));
        }

        return items;
    }

    private void Save(Dictionary<string, Item> items)
    {
        SecureFiles.EnsureDirectory(options.SecretsDirectory);
        var temp = FilePath + ".tmp";
        File.Delete(temp);
        using (var stream = new MemoryStream())
        {
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("version", 1);
                json.WriteString("keyId", keys.Ring.Current.KeyId);
                json.WriteStartObject("items");
                foreach (var (name, item) in items.OrderBy(i => i.Key, StringComparer.Ordinal))
                {
                    json.WriteStartObject(name);
                    if (item.KeyId != keys.Ring.Current.KeyId)
                    {
                        json.WriteString("keyId", item.KeyId);
                    }

                    json.WriteString("nonce", Convert.ToBase64String(item.Nonce));
                    json.WriteString("ciphertext", Convert.ToBase64String(item.Ciphertext));
                    json.WriteEndObject();
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            SecureFiles.WriteNew(temp, stream.ToArray(), readOnly: false);
        }

        AfterTempWrite?.Invoke();
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed record Item(string KeyId, byte[] Nonce, byte[] Ciphertext);
}
