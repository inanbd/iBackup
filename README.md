# iBackup — Secure Client-Server Backup

A production-oriented client-server backup system for Windows: the client watches
your folders, encrypts everything locally with **AES-256-GCM**, and streams
deduplicated, compressed, chunked uploads to a self-hosted ASP.NET Core server
backed by SQL Server. The server never sees plaintext.

```
┌────────────────────────────┐        HTTPS / JWT        ┌───────────────────────────┐
│  Windows client            │  ───────────────────────▶ │  iBackup server           │
│  ├─ WPF app (MVVM)         │   chunked, resumable,     │  ├─ ASP.NET Core Web API  │
│  ├─ Windows Service        │   encrypted uploads       │  ├─ CQRS + MediatR (VSA)  │
│  └─ Engine (Client.Core)   │ ◀───────────────────────  │  ├─ ADO.NET → SQL Server  │
│     watch → hash → zstd    │   restore downloads       │  └─ Content-addressed     │
│     → AES-256-GCM → chunks │                           │     disk storage          │
└────────────────────────────┘                           └───────────────────────────┘
```

## Repository layout

| Path | Project | Purpose |
|---|---|---|
| `src/Server/iBackup.Server.Api` | ASP.NET Core host | Controllers, JWT auth, Swagger, rate limiting, Serilog, middleware, Razor Pages admin dashboard (`/Admin`) |
| `src/Server/iBackup.Server.Application` | Application layer | Vertical slices: CQRS commands/queries, MediatR handlers, FluentValidation, per-slice ADO.NET SQL |
| `src/Server/iBackup.Server.Infrastructure` | Infrastructure | SQL connection factory, JWT + BCrypt, disk storage, audit logging, background services |
| `src/Server/iBackup.Server.Domain` | Domain | Entities, domain exceptions |
| `src/Shared/iBackup.Shared` | Contracts | DTOs and enums shared by server and clients |
| `src/Client/iBackup.Client.Core` | Client engine | Scanner, FileSystemWatcher monitor, scheduler, upload engine, crypto, compression, SQLite state, REST client |
| `src/Client/iBackup.Client.App` | WPF app | MVVM desktop UI (Login, Dashboard, Sources, Schedules, Progress, Restore, Devices, Logs, Settings) |
| `src/Client/iBackup.Client.Service` | Windows Service | Headless engine host for unattended backups |
| `database/` | SQL scripts | Idempotent schema, stored procedures, seed data |
| `tests/` | xUnit | Unit tests (server + client) and full-stack integration tests |

## Quick start

### Server

```bash
# 1. Start SQL Server (or use an existing instance)
docker compose up -d sqlserver

# 2. Run the API (Development initializes the schema from database/*.sql automatically)
cd src/Server/iBackup.Server.Api
dotnet run
# Swagger UI: https://localhost:5001/swagger
```

Or run the whole stack in containers: `docker compose up --build`.

### Admin dashboard

The server hosts a Razor Pages **admin dashboard** at `/Admin` (server-side UI is
admin-only; end users work through the desktop client). It is cookie-authenticated,
separate from the JWT scheme the API uses, and restricted to accounts with the
`IsAdmin` flag. Pages: server-wide overview (users, storage, jobs, in-flight
uploads, activity feed), user management (**create users** with an optional
custom quota and admin flag, search, per-user quotas, enable/disable with
session revocation, grant/revoke admin), per-user detail (devices, job history,
audit trail), and a filterable audit log browser.

Create the first administrator one of two ways:

- **Bootstrap an existing account** — set `Admin:BootstrapEmail` to a registered
  account's email (env var `Admin__BootstrapEmail`); it is promoted at startup.
- **Seed a default admin (dev/demo only)** — run the optional script
  `database/seed/004_seed_admin.sql`, which creates a ready-to-use admin:

  | Email | Password |
  |---|---|
  | `admin@ibackup.local` | `Admin@iBackup2026` |

  ```bash
  # against the docker SQL Server
  docker exec -i mssql /opt/mssql-tools18/bin/sqlcmd -C \
    -S localhost -U sa -P 'iBackup!Dev2026' -d iBackup \
    -i /dev/stdin < database/seed/004_seed_admin.sql
  ```

  > ⚠️ These are **public, well-known credentials** committed to the repo. They
  > exist only for local development and demos. The script is deliberately **not**
  > run by the automatic schema initializer (it lives in `database/seed/`, outside
  > the auto-run glob). **Never** run it against a production or internet-exposed
  > database, and change the password from the Users page if you do. It's
  > idempotent and never resets a password you've already changed.

Further admins are promoted from the Users page. Admins cannot disable or
demote their own account.

### IP access control

The server enforces a **default-deny IP policy**: only the local host and
whitelisted IPs may connect — every other address gets `403`. Manage it on the
admin dashboard:

- **IP access** page — maintain the whitelist and blacklist. Use `*` to
  whitelist (or blacklist) all addresses. Blacklist entries always win over the
  whitelist, and the local host can never be locked out.
- **Login attempts** page — every authentication attempt (API and dashboard)
  with its IP and outcome, filterable by IP/email/failures.
- **Auto-blacklist** — 5 failed logins from one IP (configurable) automatically
  add it to the blacklist.

Configuration (`IpAccessControl` section):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch for IP filtering |
| `TrustForwardedFor` | `false` | Take client IP from `X-Forwarded-For` — enable **only** behind a trusted proxy |
| `MaxFailedAttempts` | `5` | Failed logins from an IP that trigger auto-blacklist |
| `FailedAttemptWindowMinutes` | `60` | Window over which failures are counted |
| `CacheSeconds` | `15` | Rule-cache TTL (admin changes apply immediately regardless) |

> On a fresh install every non-local IP is blocked, so sign in from the server
> host first and whitelist your address (or `*`) before connecting clients
> remotely. Behind a reverse proxy, enable `TrustForwardedFor` so the real
> client IP — not the proxy's — is evaluated.

**Before production**: set a strong `Jwt:SigningKey` (≥ 32 bytes) and the
`ConnectionStrings:Default` via environment variables or a secret store, put the
API behind HTTPS (HSTS is enabled outside Development), and point
`Storage:RootPath` at your backup volume.

### Client

```bash
cd src/Client/iBackup.Client.App
dotnet run
```

1. Enter the server URL and sign in. Accounts are created by an administrator
   (see the admin dashboard below); the client does not self-register.
2. Add backup folders, pick schedules, exclusions and retention.
3. Backups run in the background; close the app or install the service for unattended runs:

```powershell
sc.exe create iBackup binPath="C:\path\to\iBackup.Service.exe" start=auto
sc.exe start iBackup
```

The service reuses the session the desktop app persisted (DPAPI-protected), so
sign in once with the app first.

## How a backup works

1. **Detect** — `FileSystemWatcher` events mark folders dirty; a periodic
   consistency scan catches anything the watcher missed. Schedules
   (manual / every X minutes / hourly / daily / weekly / monthly) also trigger runs.
2. **Diff** — the scanner streams files (exclusion rules applied) and compares
   size + mtime against the local SQLite index; only changed files continue.
3. **Hash** — SHA-256 of the plaintext is the file's identity.
4. **Deduplicate** — the client announces the hash; if the server already stores
   that content for the user, only metadata is written (a new version), no bytes move.
5. **Compress** — Zstandard (default), GZip, or none — streaming, per folder.
6. **Encrypt** — AES-256-GCM per chunk (`12-byte nonce ‖ ciphertext ‖ 16-byte tag`),
   chunk index bound as associated data (reordering is detected). The key is
   derived on the client from the account password (PBKDF2-SHA256, 210k iterations);
   the server never receives it.
7. **Upload** — chunks (100 MB default, configurable) go up in parallel with
   retry + exponential backoff. Interrupted uploads resume: the server reports
   which chunk indexes it already has. The server verifies each chunk's SHA-256,
   assembles the file automatically when the last chunk lands, and bills the
   user's quota.
8. **Versioning & retention** — every upload creates a new version. Retention
   (keep forever / last N versions / last N days / last N months) runs server-side;
   deleted files remain restorable until retention expires.
9. **Restore** — browse versions, pick original or custom location; the client
   downloads ciphertext, decrypts and decompresses locally.

## Security

- HTTPS only (HSTS in production), JWT access tokens (15 min) + rotating refresh
  tokens; replay of a rotated refresh token revokes the device session.
- BCrypt (work factor 12) password hashes; refresh tokens stored only as SHA-256.
- Client-side AES-256-GCM: the server stores ciphertext exclusively.
- Parameterized SQL everywhere (ADO.NET, no ORM), stored procedures on hot paths.
- Path traversal protection on every client-supplied path plus a defense-in-depth
  check at the storage layer; reserved Windows names rejected.
- Rate limiting (global per-IP + strict window on `/api/auth/*`).
- Audit log of logins, device changes, backups and restores.
- Per-user storage isolation on disk (`Storage/Users/{UserId}/...`) and in every query.

## API surface

`POST /api/auth/login|refresh|logout` (no public registration — accounts are
admin-created) ·
`POST /api/client/register` (device registration) · `GET /api/client/profile` ·
`GET|POST|PUT /api/folders` · `DELETE /api/folders/{id}` ·
`POST /api/backup/start|upload|chunk|delete-file|rename-file|finish` · `GET /api/backup/history` ·
`GET /api/restore/files` · `POST /api/restore` · `GET /api/restore/download/{versionId}` ·
`GET /api/devices` · `PUT /api/devices` · `DELETE /api/devices/{id}` · `POST /api/devices/{id}/logout` ·
`GET /api/dashboard`

Full request/response contracts live in `src/Shared/iBackup.Shared/Contracts` and
in Swagger (`/swagger` in Development).

## Tests

```bash
dotnet test                                   # unit tests (server + client engine)

docker compose up -d sqlserver                # then full-stack API tests:
export IBACKUP_TEST_DB="Server=localhost;Database=iBackup_Test;User Id=sa;Password=iBackup!Dev2026;TrustServerCertificate=True"
dotnet test tests/iBackup.Server.IntegrationTests
```

Integration tests cover the auth lifecycle (including refresh-token rotation and
reuse detection), chunked upload with out-of-order chunks, resume after a
simulated crash, per-user deduplication, restore byte-for-byte round trips and
multi-user concurrency. They no-op when `IBACKUP_TEST_DB` is not set.

All suites are verified passing: 95 unit tests plus the 5 full-stack
integration scenarios against a real SQL Server 2022 container, and a live
end-to-end run of the client engine (scan → Zstd → AES-256-GCM → chunked
upload → dedup → incremental → restore) against the running API.
CI (`.github/workflows/ci.yml`) runs the same matrix on every push.

## Design notes & future work

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full design: schema,
storage layout, upload/resume protocol, crypto format, and the extension points
for zero-knowledge encryption (user-supplied master password), differential
backups, cross-device dedup, cloud storage providers and non-Windows clients.
