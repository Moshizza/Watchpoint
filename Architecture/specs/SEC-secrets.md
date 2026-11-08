# SEC — Secrets Vault and Site Certificates

| | |
|---|---|
| Spec id | SEC |
| Projects | `src/service/Watchpoint.Secrets` |
| Milestone | M1 |
| Tasks | T1.05 |
| Design docs | [Security — Secrets vault](../../docs/operations/security.md#secrets-vault) |

## 1. Purpose and scope

- Store secrets (passwords, API keys, tokens, private keys) **encrypted**, readable only by
  the component that owns them, never displayed, never logged.
- Provide **bootstrap secrets** needed before the database is reachable (database password,
  NATS token).
- Generate and renew the **site certificate authority** and the node's TLS certificate.

Not in scope: cluster-wide master key distribution (CLU, M9), TPM and cloud KMS key
providers (added in DEP/CLU; the interface is ready), user passwords (IDN hashes them; they
are not secrets).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST, STO (configuration store), .NET cryptography (`AesGcm`, `ECDsa`, `CertificateRequest`), Windows DPAPI (`ProtectedData`, Windows only) | STO (database password), HST (NATS token, HTTPS certificate), IRT (connection secrets), IDN (token signing keys), NOT, FED… |

## 3. Project layout

```
Watchpoint.Secrets/
├── Contracts/
│   ├── SecretRef.cs, SecretKind.cs, SecretValue.cs, SecretMetadata.cs
│   ├── ISecretVault.cs
│   ├── IBootstrapSecretStore.cs
│   ├── ISiteCertificateService.cs   (+ ISecretsStartup)
│   ├── SecretsOptions.cs            node facts (data folder, node id, site name) + settings
│   ├── ISecretsAuditSink            (in ISecretVault.cs; AUD implements it from T3.06)
│   ├── SecretsServiceCollectionExtensions.cs   AddWatchpointSecrets
│   └── SecretsErrors.cs
├── Internal/
│   ├── MasterKey/IMasterKeyProvider.cs, FileMasterKeyProvider.cs, DpapiMasterKeyProvider.cs, EnvironmentMasterKeyProvider.cs
│   ├── Envelope/EnvelopeCipher.cs
│   ├── SecretVault.cs
│   ├── BootstrapSecretStore.cs
│   ├── SiteCertificateService.cs
│   └── Storage/SecretsDbContext.cs (+ Postgres subclass and migrations)
└── SecretsLog.cs              event ids 2200–2299

The host (T1.01) calls `ISecretsStartup.InitializeAsync` in phase `Infrastructure` (master key,
legacy NATS token import, certificates) and plugs `ISiteCertificateService.GetServerCertificate`
into Kestrel's `ServerCertificateSelector`. `AddWatchpointSecrets` replaces the storage module's
`IDatabasePasswordProvider`: database passwords come from `PasswordDev` (Development) or a
`bootstrap:` secret named by `Database:Databases:<key>:PasswordRef`.
```

## 4. Concepts

| Concept | Description |
|---|---|
| **Master key** (KEK) | 256-bit key protecting data keys. One active master key per node (per cluster from M9), identified by `KeyId` (8 hex chars = first 4 bytes of SHA-256 of the key) |
| **Data key** (DEK) | Random 256-bit key per secret version, encrypted with the master key |
| **Secret** | Encrypted value + metadata, stored in the configuration database (`wp_secrets.secrets`) |
| **Bootstrap secret** | Encrypted value stored in a node-local file, for use before the database is available |
| **Owner** | `(module, entityId?)` that may read a secret, e.g. (`Integrations`, connection id) |

## 5. Public contract

```csharp
public enum SecretKind { Password, ApiKey, Token, Certificate, PrivateKey, Generic }

// Text form: "vault:<uuid>" or "bootstrap:<name>"  (name pattern ^[a-z][a-z0-9.-]{1,63}$)
public readonly record struct SecretRef
{
    public static Result<SecretRef> Parse(string text);
    public bool IsBootstrap { get; }
    public SecretId? VaultId { get; }
    public string? BootstrapName { get; }
}

public sealed class SecretValue : IDisposable
{
    public int Length { get; }
    public T Use<T>(Func<ReadOnlySpan<byte>, T> use);          // bytes
    public T UseAsString<T>(Func<string, T> use);              // UTF-8 decoded; string not cached
    public override string ToString() => "***";               // never reveals
    public void Dispose();                                     // zeroes the buffer (CryptographicOperations.ZeroMemory)
}

public sealed record SecretOwner(string Module, Guid? EntityId);

public sealed record SecretMetadata(SecretId Id, string Name, SecretKind Kind, SecretOwner Owner,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastAccessedAt,
    DateTimeOffset? ExpiresAt, int Version);

public interface ISecretVault
{
    Task<Result<SecretId>> CreateAsync(string name, SecretKind kind, SecretOwner owner,
        ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct);
    Task<Result> ReplaceAsync(SecretId id, SecretOwner caller, ReadOnlyMemory<byte> value,
        DateTimeOffset? expiresAt, CancellationToken ct);                     // new version
    Task<Result<SecretValue>> ReadAsync(SecretId id, SecretOwner caller, string purpose, CancellationToken ct);
    Task<Result> DeleteAsync(SecretId id, SecretOwner caller, CancellationToken ct);
    Task<Result<SecretMetadata>> GetMetadataAsync(SecretId id, CancellationToken ct);
    Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretOwner? owner, CancellationToken ct);
    Task<Result<int>> RewrapAllAsync(CancellationToken ct);   // re-encrypt all DEKs with the current master key
}

public interface IBootstrapSecretStore
{
    Result<SecretValue> Read(string name);
    Result Write(string name, ReadOnlySpan<byte> value);   // creates or replaces
    bool Exists(string name);
}

public interface ISiteCertificateService
{
    X509Certificate2 GetServerCertificate();           // for Kestrel (ServerCertificateSelector)
    X509Certificate2 GetCaCertificate();               // public part, downloadable by clients
    string GetCaFingerprintSha256();                   // shown in clients' trust prompt
    Task<X509Certificate2> IssueAsync(CertificateRequestSpec spec, CancellationToken ct); // mTLS for nodes/hosts (M4/M9)
}
public sealed record CertificateRequestSpec(string CommonName, IReadOnlyList<string> DnsNames,
    IReadOnlyList<IPAddress> IpAddresses, CertificateUsage Usage, TimeSpan Validity);
public enum CertificateUsage { Server, Client, ServerAndClient }
```

Ownership rule: `ReadAsync`, `ReplaceAsync`, `DeleteAsync` succeed only if `caller` equals
the secret's owner (module name ordinal match; entity id equal or secret owner entity null).
Otherwise `secrets.access.denied` — and an audit event is emitted.

## 6. Cryptography

### 6.1 Envelope encryption

For each secret version:

1. Generate DEK (32 random bytes, `RandomNumberGenerator`).
2. `ciphertext = AES-256-GCM(key=DEK, nonce=random 12 bytes, plaintext=value,
   aad = UTF8("wp-secret|" + id + "|" + version))`, tag 16 bytes appended to ciphertext.
3. `wrappedDek = AES-256-GCM(key=MasterKey, nonce=random 12 bytes, plaintext=DEK,
   aad = UTF8("wp-dek|" + id + "|" + version + "|" + keyId))`.
4. Store `key_id, wrapped_dek, dek_nonce, ciphertext, nonce`; zero DEK and plaintext buffers.

Decryption reverses this; any authentication failure → `secrets.integrity.failed` (and log
1211 Error). Values up to 64 KB.

### 6.2 Master key providers

```csharp
internal interface IMasterKeyProvider
{
    string Name { get; }                       // "file", "dpapi", "environment"
    bool IsAvailable();
    MasterKey LoadOrCreate();                  // creates on first start where allowed
}
internal sealed class MasterKey : IDisposable { public string KeyId { get; } public ReadOnlySpan<byte> Bytes { get; } }
```

| Provider | Selection | Storage | Notes |
|---|---|---|---|
| `dpapi` | Default on Windows | `<Data>/secrets/master.key.dpapi` = DPAPI(LocalMachine, entropy = UTF8("watchpoint-master|" + NodeId)) | Only processes on this machine can unprotect; file ACL SYSTEM + service account |
| `file` | Default on Linux | `<Data>/secrets/master.key` raw 32 bytes, mode 0400, owner service account | Warn (2203) if permissions are wider |
| `environment` | Containers: `Secrets:MasterKeyProvider=environment` | `WATCHPOINT_MASTER_KEY` (base64, 32 bytes) or `WATCHPOINT_MASTER_KEY_FILE` (path) | Never created automatically |

Settings `Secrets:MasterKeyProvider` overrides the default. The vault holds a key ring: the
current master key plus retired keys still able to unwrap data keys until `RewrapAllAsync` has
run (each stored item records the `key_id` that wraps it). Changing provider is done by
`wpctl secrets rewrap --to <provider>` (DEP): load old key, create new, re-wrap all DEKs and
bootstrap file, then delete the old key.

### 6.3 Bootstrap secret store

- File `<Data>/secrets/bootstrap.json`:
  `{ "version": 1, "keyId": "…", "items": { "<name>": { "nonce": "b64", "ciphertext": "b64" } } }`,
  each item encrypted directly with the master key (AES-GCM, AAD = `"wp-bootstrap|" + name`).
- Written atomically (write temp file, flush, rename). Mode 0600 / ACL as master key.
  An item sealed with another key than the file's `keyId` carries its own `"keyId"` (during a
  rewrap).
- Standard names: `database.<key>` (database passwords), `nats.token`, `cluster.join` (M9).
- Migration step (runs once, phase Infrastructure): if `<Data>/nats/token` exists (HST
  §7.4), import it as `nats.token` and delete the file.

## 7. Site certificates

At first start (phase `Infrastructure`, after the vault is ready — the CA private key is a
**bootstrap** secret because Kestrel needs it before the database):

1. Create the **site CA**: ECDSA P-256, subject `CN=Watchpoint Site CA <SiteName or NodeId>`,
   validity 10 years, BasicConstraints CA=true pathlen=0, KeyUsage keyCertSign+cRLSign.
   Private key → bootstrap secret `site-ca.key` (PKCS#8 DER); certificate →
   `<Data>/certs/site-ca.crt` (PEM, public).
2. Issue the **node server certificate**: ECDSA P-256, validity 2 years, SAN = machine DNS
   name(s), `localhost`, all non-loopback IP addresses of the machine, plus
   `Http:Certificate:ExtraNames`; EKU serverAuth. Private key → bootstrap secret
   `node-tls.key`; certificate → `<Data>/certs/node.crt`.
3. If `Http:Certificate:Path` is configured (customer certificate, PFX or PEM+key), it is
   used instead of the generated node certificate (the CA is still generated for mTLS).

Renewal: a background check every 12 h; when the node certificate expires in < 30 days,
re-issue and hot-swap in Kestrel (`ServerCertificateSelector` reads the current one). Log
2240. CA expiry < 1 year → warning 2241 and system event (HLT).

`GET /api/v1/system/ca` (anonymous, API module) returns the CA certificate (PEM) and its
SHA-256 fingerprint so clients can pin it (DSK, MOB).

## 8. Data (`wp_secrets` schema)

```sql
CREATE TABLE wp_secrets.secrets (
  id               uuid PRIMARY KEY,
  name             text        NOT NULL,           -- for humans, e.g. "Salto A password"
  kind             text        NOT NULL,
  owner_module     text        NOT NULL,
  owner_entity     uuid        NULL,
  version          int         NOT NULL,
  key_id           char(8)     NOT NULL,
  wrapped_dek      bytea       NOT NULL,
  dek_nonce        bytea       NOT NULL,
  ciphertext       bytea       NOT NULL,
  nonce            bytea       NOT NULL,
  expires_at       timestamptz NULL,
  last_accessed_at timestamptz NULL,
  created_at       timestamptz NOT NULL,
  updated_at       timestamptz NOT NULL,
  row_version      bigint      NOT NULL
);
CREATE INDEX secrets_owner ON wp_secrets.secrets (owner_module, owner_entity);
```

Only the latest version is kept (replace overwrites; history of values is not retained).
`last_accessed_at` is updated at most once per minute per secret (to avoid write load).

## 9. Settings

| Key | Type | Default |
|---|---|---|
| `Secrets:MasterKeyProvider` | enum | `dpapi` on Windows, `file` otherwise |
| `Secrets:MaxValueBytes` | int | 65536 |
| `Http:Certificate:ExtraNames` | list | [] |
| `Http:Certificate:Path` / `:KeyPath` / `:PasswordRef` | strings | empty |

## 10. Errors

| Code | Kind | When |
|---|---|---|
| `secrets.not_found` | NotFound | Unknown id or bootstrap name |
| `secrets.access.denied` | Forbidden | Caller is not the owner |
| `secrets.integrity.failed` | Internal | Decryption authentication failed |
| `secrets.value.too_large` | Validation | Value > MaxValueBytes |
| `secrets.master_key.unavailable` | Unavailable | Provider cannot load the key (start-up fails) |
| `secrets.ref.invalid` | Validation | Bad `SecretRef` text |

## 11. Observability

| EventId | Level | Message |
|---|---|---|
| 2201 | Information | Master key {KeyId} loaded from provider {Provider} |
| 2202 | Information | Master key {KeyId} created with provider {Provider} |
| 2203 | Warning | Master key file permissions too wide: {Path} |
| 2210 | Debug | Secret {SecretId} read by {Module} for {Purpose} |
| 2211 | Error | Secret {SecretId} integrity check failed |
| 2212 | Warning | Secret {SecretId} access denied for {Module} |
| 2220 | Information | Rewrapped {Count} data keys to master key {KeyId} |
| 2230 | Information | Legacy NATS token imported as bootstrap secret nats.token |
| 2231 | Information | Secret audit: {Action} {SecretId} by {Module} ({Count}) — logging audit sink used until AUD exists |
| 2240 | Information | Node certificate renewed, expires {NotAfter} |
| 2241 | Warning | Site CA expires on {NotAfter} |
| 2242 | Information | Site CA {Subject} created, fingerprint {Fingerprint} |

Metrics: `watchpoint.secrets.reads` (counter, tag `module`), `watchpoint.secrets.denied`
(counter), `watchpoint.secrets.cert.days_left` (gauge, tag `cert`).

Audit: every create/replace/delete/denied read produces an audit record through
`ISecretsAuditSink` (implemented by AUD from T3.06; until then a logging implementation, event
2231, is registered).
Reads are audited in aggregate (one record per secret per hour with a count), to avoid
flooding.

## 12. Security

- Secret values never appear in logs, exceptions, metrics, API responses or diagnostics.
- `SecretValue` buffers are pinned (`GC.AllocateArray(pinned: true)`) and zeroed on dispose.
- The API never returns secret values; configuration UIs show "•••• (set)" and allow
  replacement only.
- Threats handled: database dump theft (values unreadable without the node's master key),
  tampering (GCM authentication + AAD binding id/version), cross-module misuse (owner check).

## 13. Tests

Unit: `tests/service/Watchpoint.Secrets.Tests`. Integration: `Watchpoint.IntegrationTests/Secrets`.

| Id | Layer | Test | Given / When / Then |
|---|---|---|---|
| SEC-U-001 | U | Envelope round-trip | Encrypt "p@ss" → decrypt → equal bytes |
| SEC-U-002 | U | Nonces unique | 10,000 encryptions → no repeated nonce |
| SEC-U-003 | U | Tampered ciphertext detected | Flip one bit → `secrets.integrity.failed` |
| SEC-U-004 | U | AAD binds id and version | Ciphertext of id A presented as id B → integrity failure |
| SEC-U-005 | U | Wrong master key detected | Different key → integrity failure |
| SEC-U-006 | U | Value size limit | 65,537 bytes → `secrets.value.too_large` |
| SEC-U-007 | U | Owner check on read | Owner (Integrations, conn1); caller (Integrations, conn2) → `secrets.access.denied`; audit sink called |
| SEC-U-008 | U | Owner with null entity readable by module | Owner (Identity, null), caller (Identity, any) → success |
| SEC-U-009 | U | `SecretValue.ToString` hides value | → `"***"` |
| SEC-U-010 | U | `SecretValue.Dispose` zeroes buffer | After dispose, internal buffer all zero (test hook) |
| SEC-U-011 | U | `SecretRef.Parse` | `vault:<uuid>`, `bootstrap:database.default` ok; `vault:x`, `foo:bar`, `bootstrap:Bad Name` → `secrets.ref.invalid` |
| SEC-U-012 | U | File key provider creates 0400 key | Linux path → 32 bytes, mode 0400; second load returns same KeyId |
| SEC-U-013 | U | File key permissions warning | Mode 0644 → warning 2203 |
| SEC-U-014 | U | Environment provider | `WATCHPOINT_MASTER_KEY` base64 of 32 bytes → loads; 31 bytes → `secrets.master_key.unavailable`; never creates |
| SEC-U-015 | U | DPAPI provider (CI job "Secrets tests (Windows)"; skipped on Linux) | Create then load → same KeyId; file not readable as raw key |
| SEC-U-016 | U | Bootstrap store atomic write | Simulated crash between temp write and rename → old file intact and readable |
| SEC-U-017 | U | Bootstrap NATS token migration | `<Data>/nats/token` exists → imported as `nats.token`, file deleted |
| SEC-U-018 | U | CA and node certificate generation | First start → CA (CA=true, pathlen 0, P-256, 10 y), node cert (SAN includes localhost and IPs, EKU serverAuth, chains to CA) |
| SEC-U-019 | U | Renewal at < 30 days | TestClock near expiry → new cert issued, selector returns new cert, log 2240 |
| SEC-U-020 | U | Customer certificate overrides | `Http:Certificate:Path` set → selector returns customer cert; CA still generated |
| SEC-I-001 | I | Vault persists and reads after restart | Create secret, new vault instance (same DB, same master key) → read ok |
| SEC-I-002 | I | Rewrap to a new master key | Create 50 secrets; rotate key; `RewrapAllAsync` → 50; all readable with new key; old key removed → still readable |
| SEC-I-003 | I | Database dump does not contain plaintext | Create secret "UniqueP@ss123"; textual dump of every row of `wp_secrets` (`row::text`) and the raw bytes of every `bytea` column → string not found (no `pg_dump` binary needed in the test) |
| SEC-I-004 | I | No secret in logs | `SecretLeakDetector` registered with values; full create/read/replace cycle → no leak |
| SEC-I-005 | I | `last_accessed_at` throttled | 100 reads within a minute → one update |

## 14. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.05 | All | SEC-U-001…SEC-U-020, SEC-I-001…SEC-I-005 |
