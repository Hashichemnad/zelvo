# Encryption at rest for stored secrets (task A5)

Covers the non-functional requirement **Encryption** ("AES-256 at rest with keys in a managed key vault") for secrets that Zelvo stores in its database: AI provider keys (module 9, task E4), payment gateway credentials (module 15, E8), connector passwords (module 14, E7). Product owner decision 2026-11-18: the master key comes from a **key file** (small installs) or **HashiCorp Vault / OpenBao Transit** (medium and large installs). Code: `src/api/Zelvo.Secrets`.

## Envelope encryption

```
secret ──AES-256-GCM──▶ "zv1.…" (stored in the module's column)
           ▲
   tenant data key (32 random bytes)  ──wrapped by──▶  key-encryption key
   stored only wrapped in tenant_data_keys              master key file  or  Vault/OpenBao Transit key
```

- **Data keys.** Each tenant has one active AES-256 data key, created on first use (`RandomNumberGenerator`). `tenant_data_keys` stores only the wrapped key and the id of the key-encryption key that wrapped it (`kek_id`). The table is tenant-owned under forced row-level security; a trigger refuses deletes, because a deleted data key would make that tenant's secrets unreadable (keys are retired instead). Creating a data key is audited (`secrets.dataKeyCreated`, security category).
- **Values.** `ISecretProtector.ProtectAsync(purpose, plaintext)` returns `zv1.` followed by base64url of the data key id, a 96-bit nonce, the ciphertext and the 128-bit tag. The associated data is `zelvo/secret/v1|<tenant id>|<purpose>`, so a value copied to another tenant or to another field (purpose) does not decrypt, and any change is detected. Purposes are short names such as `ai.provider.apiKey` or `payments.gateway:<id>.secret`.
- **Unwrapped data keys** are kept in memory for 10 minutes (`Secrets:DataKeyCacheDuration`), so Vault is not called for every value; the cache is per process and never written to disk.
- **Who decrypts.** Only the API and the worker, for the current tenant (`ITenantContext`). Modules that show or use a secret decide whether the use is a "view of sensitive data" to audit; secrets are never returned by the API in clear (E4 and E8 show only whether a value is set).

## Key-encryption keys

| Provider | Setting | Key id | Notes |
| --- | --- | --- | --- |
| Key file | `Secrets:Provider=File`, `Secrets:File:Path` | `file:` + first 16 hex digits of the key's SHA-256 | Base64 of 32 random bytes (`openssl rand -base64 32`), mounted as a Docker or Kubernetes secret. Data keys wrapped with AES-256-GCM. |
| Vault or OpenBao | `Secrets:Provider=Vault`, `Secrets:Vault:Address`, `TransitMount` (`transit`), `KeyName` (`zelvo`) | `vault:<mount>/<key>` | Transit `encrypt`/`decrypt`/`rewrap`; the key never leaves Vault. Sign-in with `TokenFile`, or AppRole (`RoleIdFile`, `SecretIdFile`, `AppRoleMount`); `Namespace` for Vault Enterprise; `CaFile` for a private CA. |

The Vault policy Zelvo needs is only:

```hcl
path "transit/encrypt/zelvo" { capabilities = ["update"] }
path "transit/decrypt/zelvo" { capabilities = ["update"] }
path "transit/rewrap/zelvo"  { capabilities = ["update"] }
```

Every configured key can unwrap; only the provider in `Secrets:Provider` wraps new data keys. Earlier key files go in `Secrets:File:PreviousPaths:<n>`. The readiness check `keyVault` wraps and unwraps a random value with the current key (so it also shows when Vault is unreachable or refuses the token).

## Rotation and moving to Vault

`dotnet Zelvo.Api.dll rewrap-keys [--retire-active]` (needs `ConnectionStrings:Postgres`, `ConnectionStrings:PostgresOwner` and the `Secrets` settings), tenant by tenant with row-level security:

- **New master key file:** set `Secrets:File:Path` to the new file and the old one in `PreviousPaths`, run `rewrap-keys`, then remove the old file.
- **Key file to Vault:** set `Secrets:Provider=Vault` with the Vault settings and keep `Secrets:File:Path`, run `rewrap-keys`, then remove the key file.
- **Transit key rotated in Vault** (`transit/keys/zelvo/rotate`): run `rewrap-keys`; Vault moves each data key to the latest key version without Zelvo seeing it.
- `--retire-active` also retires each tenant's active data key, so new secrets get a new one; values written earlier still decrypt.

Stored values are not re-encrypted: they keep their data key, which is only wrapped again. Rewraps are audited (`secrets.dataKeysRewrapped`).

## Compose (development)

`scripts/dev-up.sh` creates `deploy/compose/secrets/master.key` (git-ignored) once; the API and worker get it as the Compose secret `zelvo-master-key`. It is readable by the containers' non-root users, which is acceptable for development only; the hardened install (task F6) sets ownership and offers Vault. Deleting the file makes stored secrets unreadable; `make reset` keeps it.

## What is not covered here

- **Database files and backups.** PostgreSQL has no built-in transparent encryption; installs encrypt the volume (for example LUKS, or the cloud provider's disk encryption). Backups are encrypted by pgBackRest (task F6, `backup-restore.md`). Secrets are additionally encrypted by Zelvo as above.
- **Service passwords** (PostgreSQL, RabbitMQ, OpenSearch, Valkey) are environment values: from `.env` in development and in the hardened Compose install (made by `install.sh` with random values, mode 0600, root only; the master key file is readable only by the services' user), and from a Kubernetes Secret in Helm installs (task F6). A secrets manager for them is not part of Phase 1.

## Tests

`SecretsTests` (real PostgreSQL): file keys, round trip, binding to tenant and purpose, tampering, row-level security, no deletes, rotation to a new key file. `VaultTransitTests` (real OpenBao in development mode): token and AppRole sign-in with the least-privilege policy, Transit key rotation and rewrap, refused tokens.
