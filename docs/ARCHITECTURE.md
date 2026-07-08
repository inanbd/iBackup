# iBackup Architecture

## Server

### Layering (Clean Architecture + Vertical Slices)

```
iBackup.Server.Api            controllers, auth, middleware, composition root
   │
iBackup.Server.Application    one folder per feature (vertical slice):
   │                          command/query + validator + handler + its SQL
iBackup.Server.Domain         entities, enums, AppException
iBackup.Server.Infrastructure ISqlConnectionFactory, IJwtTokenService,
                              IPasswordHasher, IFileStorage, IAuditLogger,
                              background services
```

Every API request follows the same path:

```
Controller → MediatR ISender → LoggingBehavior → ValidationBehavior (FluentValidation)
           → slice handler → ADO.NET (parameterized SQL / stored procedure)
```

Slices own their SQL. Cross-slice reuse is limited to deliberately shared data
classes (`RefreshTokenData`, `BackupData`, `FolderData`) that live next to the
slices that use them — no generic repository layer, no ORM.

Errors are surfaced with `AppException` (carries HTTP status + stable error code)
and translated to a JSON `ApiError` by `ExceptionHandlingMiddleware`; FluentValidation
failures map to 400 with per-field details.

### Database

Schema in `database/001_schema.sql` (idempotent), procedures in `002_procedures.sql`.

Tables: `Users`, `RefreshTokens`, `Devices`, `BackupFolders`, `BackupJobs`,
`Files`, `FileVersions`, `StorageObjects`, `UploadSessions`, `UploadChunks`,
`RestoreRequests`, `AuditLogs`, `ApplicationSettings`.

Scalability decisions:

- `Files` is looked up by `(UserId, DeviceId, FolderId, RelativePathHash)` where
  `RelativePathHash` is a persisted computed `BINARY(32)` SHA-256 of the
  lower-cased path — the unique index stays 32 bytes wide per key no matter how
  long paths get, which matters at millions of rows.
- `StorageObjects` is the content-addressed store: unique `(UserId, Sha256)`.
  `FileVersions` reference storage objects; `ReferenceCount` tracks how many
  versions share one blob (per-user deduplication).
- Hot paths are stored procedures run inside transactions:
  - `usp_RegisterUploadChunk` — idempotent chunk registration + progress.
  - `usp_CommitFileVersion` — creates/updates the logical file, assigns the next
    version number under `UPDLOCK/HOLDLOCK`, bumps references.
  - `usp_AbortAbandonedUploads` — cleanup sweep.
  - `usp_ApplyRetention` — expires versions per policy and returns orphaned
    blobs for disk deletion.

### Storage layout

```
Storage/Users/{UserId}/Chunks/{SessionId}/{000042}.chunk   in-flight uploads
Storage/Users/{UserId}/Files/{aa}/{bb}/{sha256}.obj        assembled encrypted objects
```

Only server-generated names (GUIDs, hashes) touch the filesystem; client-supplied
paths exist solely as database metadata. `DiskFileStorage.ResolveUserPath` still
verifies every resolved path stays under the user root (defense in depth).

### Upload protocol

```
POST /api/backup/start                → BackupJobId
POST /api/backup/upload   (announce)  → { Deduplicated, FileVersionId }            // hash already stored
                                      | { UploadSessionId, ReceivedChunkIndexes }  // new or resumed
POST /api/backup/chunk?sessionId&chunkIndex&sha256   (raw octet-stream body)
                                      → { Received, FileCompleted, FileVersionId }
POST /api/backup/delete-file | rename-file           (incremental metadata)
POST /api/backup/finish               (job statistics)
```

- Chunks stream straight to disk while being SHA-256-hashed; a mismatch against
  the declared hash rejects the chunk (client retries).
- Chunk registration is idempotent (`MERGE`), so retries and parallel uploads are safe.
- The final chunk triggers assembly: chunks are concatenated in index order into
  the content-addressed object, the version is committed, quota is billed, chunks
  are deleted. A unique-index race (two uploads of identical content) is resolved
  by keeping the winner and dropping the duplicate object.
- `AbandonedUploadCleanupService` aborts sessions idle past the cutoff (48 h
  default) and deletes their chunks.

### Background services

- `AbandonedUploadCleanupService` — see above.
- `RetentionPolicyService` — periodically applies every folder's retention,
  deletes orphaned storage objects from disk and returns the bytes to the
  user's quota.

## Client

### Engine (`iBackup.Client.Core`)

```
BackupEngine ── orchestrates ──▶ FileScanner        streaming enumeration + filters
     │                          ChangeMonitor       FileSystemWatcher + debounce
     │                          BackupScheduler     pure next-run math (unit-tested)
     │                          LocalStateStore     SQLite (raw ADO.NET): file index + queue
     └────── drives ──────────▶ UploadEngine        hash → compress → encrypt → chunks
                                RestoreService      download → decrypt → decompress
                                BackupApiClient     REST + automatic token refresh
```

- **Change detection**: watcher events mark folders dirty (renames are tracked
  explicitly and replayed via `rename-file`); the engine loop (15 s tick) runs
  incremental backups for dirty/scheduled folders. A consistency scan (4 h
  default) re-diffs everything, so missed watcher events can never lose data.
- **Incremental diff**: size + mtime against the SQLite index; deletions are
  detected as indexed paths not seen by the scan and reported as metadata.
- **Resume**: server-side (chunk indexes already received) plus client-side
  (persisted queue + rescan on restart).
- **Pause/Resume/Cancel**: a pause gate awaited between files; per-run
  cancellation token for cancel; `RunNow()` for immediate backups.
- **Bounded memory**: everything streams through fixed 128 KB buffers — a
  100 GB file never occupies more than a few megabytes of RAM.

### Crypto format

```
key        = PBKDF2-SHA256(password, SHA256("iBackup:" + lowercase(email)), 210k iters, 32 bytes)
chunk_i    = nonce(12) ‖ AES-256-GCM(key, nonce, plaintext_i, AAD = chunk index) ‖ tag(16)
file       = chunk_0 ‖ chunk_1 ‖ ... (every plaintext chunk exactly ChunkSize bytes except the last)
```

Boundaries are computable from `ChunkSize` alone, so restore needs no side
metadata. The AAD binding makes chunk reordering detectable. The same
password+email always derives the same key, so any of the user's devices can
restore. **Trade-off**: a password change requires re-encryption (or key
wrapping — see future work). The derived key is cached DPAPI-protected so the
Windows service can run unattended.

### Session persistence

`credentials.dat` (DPAPI, per-user scope) stores server URL, email, current
refresh token, device id and the derived key. `BackupApiClient` refreshes access
tokens proactively (30 s margin) and on 401, single-flight via `TokenStore`;
rotated refresh tokens are persisted immediately.

## Future work / extension points

- **Zero-knowledge master password**: wrap a random file key with a key derived
  from a separate master password; store only the wrapped key server-side.
  `EncryptionMethod` enum and per-version metadata already leave room.
- **Differential backup**: `BackupType.Differential` is reserved; the diff logic
  needs an anchor-version comparison instead of last-version.
- **Cross-device dedup**: drop `UserId` from the `StorageObjects` unique key and
  storage path (requires convergent encryption to be useful — documented trade-off).
- **Cloud storage providers**: implement `IFileStorage` against S3/Azure Blob;
  the interface is already stream-based.
- **Linux/macOS clients**: `iBackup.Client.Core` targets plain `net10.0`;
  only `CredentialStore` (DPAPI) needs a platform alternative.
- **Web admin**: the API is UI-agnostic; add an admin area + role claims.
