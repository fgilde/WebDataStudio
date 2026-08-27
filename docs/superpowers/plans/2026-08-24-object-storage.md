# Object storage as a connection — implementation plan

> Executed inline in the session that wrote it. Steps are checkboxes so progress is visible; the
> per-step code is in the commits rather than duplicated here.

**Goal:** S3-compatible, Azure Blob, Google Cloud Storage and a plain folder as connections that can
be browsed, previewed, written to under a confirmation, and queried as tables.

**Architecture:** One connection with two faces. `StorageDriver` implements `IDbDriver`: it answers
introspection from an `IObjectStore` and hands SQL to a DuckDB session that carries the connection's
credentials as DuckDB secrets. One new interface member — `FromClause` — turns an object reference
into `read_parquet('…')`, which is what makes the existing grid, filter language, export, charts and
masking work on a file.

**Tech stack:** .NET 10, DuckDB.NET 1.5.5 (`httpfs`, `azure` extensions bundled), AWSSDK.S3,
Azure.Storage.Blobs + Azure.Identity, Google.Cloud.Storage.V1, React 19 + Mantine, xunit v3 +
Testcontainers (MinIO, Azurite), Playwright.

**Spec:** `docs/superpowers/specs/2026-08-24-object-storage-design.md`

## Global constraints

- One engine id: `storage`. The URL scheme picks the provider: `s3://`, `azblob://`, `gs://`, `file://`.
- Nothing walks a bucket. Listing is paged by cursor, always.
- Writes go through a confirmation that names the object, its size and the connection; refused on a
  read-only connection and on one coloured red (production).
- Credentials: absent from the URL means the machine's own identity; present means stored encrypted
  through `SecretProtector`, as every other connection secret already is.
- DuckDB never reaches the internet: `SET extension_directory`, auto-install off, `LOAD` from the
  bundle.
- Features become F28.1–F28.8 in `docs/features.md` and the spec's table; the coverage test enforces it.
- Documentation in both repositories, English and German, per the standing rule.

---

### Task 1: The store interface, the local provider, and the URL

**Files:** `src/WebDataStudio.Server/Storage/IObjectStore.cs`, `StorageModels.cs`,
`LocalObjectStore.cs`, `StorageUrl.cs`; modify `Services/EngineGuess.cs`, `Services/ConnectionUrl.cs`;
test `tests/…/Storage/StorageUrlTests.cs`, `Storage/LocalObjectStoreTests.cs`.

**Produces:** `IObjectStore` (`ListAsync`, `HeadAsync`, `OpenReadAsync`, `WriteAsync`, `DeleteAsync`,
`SqlUri`, `SecretStatement`), `StoragePage`, `StorageEntry`, `StorageObject`,
`StorageUrl.Parse(string) → StorageTarget(Provider, Account, Container, Prefix, Options)`.

- [ ] Failing tests for `StorageUrl.Parse` over the four schemes, credentials, endpoint, and refusals
- [ ] `StorageUrl` + models + interface
- [ ] `LocalObjectStore` over a temp directory, with traversal outside the root refused
- [ ] `EngineGuess`/`ConnectionUrl` answer `storage` for the four schemes
- [ ] Tests green, commit

### Task 2: S3

**Files:** `Storage/S3ObjectStore.cs`; test `Storage/S3ObjectStoreTests.cs` (Testcontainers MinIO).

- [ ] Contract test against MinIO: list a prefix, page it, head, read, write, delete
- [ ] `S3ObjectStore` with `ServiceURL` + path-style for non-AWS endpoints, instance role when no keys
- [ ] `SqlUri` → `s3://bucket/key`, `SecretStatement` → `CREATE SECRET (TYPE s3, …)`
- [ ] Tests green, commit

### Task 3: Azure Blob

**Files:** `Storage/AzureBlobObjectStore.cs`; test `Storage/AzureBlobObjectStoreTests.cs` (Azurite).

- [ ] Contract test against Azurite, same six operations
- [ ] Key, SAS and `DefaultAzureCredential`; `SqlUri` → `az://container/key`
- [ ] `SecretStatement` → `CREATE SECRET (TYPE azure, …)`, `PROVIDER credential_chain` without a key
- [ ] Tests green, commit

### Task 4: Google Cloud Storage

**Files:** `Storage/GcsObjectStore.cs`; test `Storage/GcsObjectStoreTests.cs`.

- [ ] Pure tests: `SqlUri` → `gs://bucket/key`, HMAC secret statement, and the stated limit that a
      service account alone browses but does not query
- [ ] `GcsObjectStore` on `Google.Cloud.Storage.V1`, ADC when no HMAC keys
- [ ] Tests green, commit

### Task 5: The driver

**Files:** `Drivers/Storage/StorageDriver.cs`, `Drivers/Storage/StorageSession.cs`,
`Drivers/Storage/DuckDbExtensions.cs`; modify `Drivers/DriverRegistry.cs`.
Test `Storage/StorageDriverTests.cs`.

- [ ] Tests: the tree lists containers then prefixes and objects (MinIO); capabilities are honest;
      the extension preamble is exactly the four statements; loading works from a staged directory
- [ ] `StorageSession` opens DuckDB in memory, sets the extension directory, loads `httpfs`/`azure`,
      registers the store's secret
- [ ] `StorageDriver`: introspection from the store, `DescribeAsync` on an object (size, type,
      modified, ETag; Parquet columns through `parquet_schema`), SQL through DuckDB
- [ ] Registered in `DriverRegistry`, capability matrix honest (`Ddl=false`, `TabularBrowse=true`)
- [ ] Tests green, commit

### Task 6: A file as a table

**Files:** modify `Drivers/Abstractions/IDbDriver.cs` (+ `FromClause` with a default),
`Endpoints/DataEndpoints.cs`; `Drivers/Storage/StorageReader.cs`.
Test `Storage/StorageReaderTests.cs`, `Editing/StorageBrowseTests.cs`.

- [ ] Tests: reader choice per extension, no `FROM` for an unreadable one, a glob for a prefix
- [ ] `FromClause` default returns the qualified name; storage returns the reader call
- [ ] `DataEndpoints` uses it; browse, sort, filter and page a Parquet in MinIO end to end
- [ ] Tests green, commit

### Task 7: Endpoints for the objects themselves

**Files:** `Endpoints/StorageEndpoints.cs`; modify `Program.cs`.
Test `Storage/StorageEndpointTests.cs`.

- [ ] Tests: preview is bounded; download streams; upload and delete refused on read-only and on
      production; a delete confirmation reports what it would remove
- [ ] `GET /api/storage/{conn}/preview`, `/download`, `POST /upload`, `POST /delete`
- [ ] Tests green, commit

### Task 8: The studio

**Files:** `web/src/api.ts`, `web/src/storage/StoragePreview.tsx`, `StorageActions.tsx`,
`web/src/explorer/nodeIcons.tsx`, `contextActions.ts`, `ObjectDetailPanel.tsx`, `dock/DockShell.tsx`.
Test `web/src/storage/*.test.ts`.

- [ ] Node kinds and icons; preview tab; menu: open data, query as table, download, upload, delete
- [ ] Upload and delete dialogs that name what happens before it happens
- [ ] Unit tests for the preview's format choice; `npm run build`, vitest, oxlint green; commit

### Task 9: The extensions in the image

**Files:** `Dockerfile`; modify `Drivers/Storage/DuckDbExtensions.cs` (path from configuration).

- [ ] Build stage downloads `httpfs` and `azure` for the image's platform into `/opt/duckdb/extensions`
- [ ] `WDS_DUCKDB_EXTENSION_DIR` overrides it; absent falls back to DuckDB's own behaviour
- [ ] Image builds, a storage connection queries a MinIO parquet inside the container; commit

### Task 10: Aspire

**Files:** `Nextended.Aspire.Hosting.WebDataStudio/Builders/WebDataStudioStorageExtensions.cs`;
modify `Resources/WebDataStudioResource.cs`. Test `WebDataStudioStorageTests.cs`.

- [ ] Tests: `WithStorage(name, url)` writes `WDS_CONN_<NAME>`; `WithBlobStorage(resource)` takes the
      app host's blob endpoint; a name that is not a connection name is refused
- [ ] Implement both, list the connection on the resource like the others
- [ ] Tests green, commit

### Task 11: Words and pictures

- [ ] `docs/features.md` F28.1–F28.8, spec table rows, engine matrix column
- [ ] `docs/guide/storage.md` + sidebar, German mirror, env table both languages
- [ ] Nextended README + `docs/projects/aspire-webdatastudio.md`
- [ ] Screenshots: the storage tree, a Parquet preview, a file queried as a table
- [ ] `smoke:storage`, development guide, link check; commit and push

## Self-review

- Spec coverage: F28.1 → Tasks 1–4; F28.2 → Tasks 1, 5; F28.3 → Tasks 5, 7, 8; F28.4 → Tasks 5, 6;
  F28.5 → Tasks 7, 8; F28.6 → Tasks 1–4; F28.7 → Tasks 5, 9; F28.8 → Task 10.
- No placeholders: every task names its files, its tests and its deliverable.
- Types consistent: `IObjectStore`, `StoragePage`, `StorageTarget`, `FromClause` are used under those
  names throughout.
