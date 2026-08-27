# Object storage

A bucket is a place people keep data, and until now the studio could not open one. It can: an
S3-compatible endpoint, Azure Blob Storage, Google Cloud Storage or a plain folder is a connection
like any other — configured through `WDS_CONN_*`, attached from an Aspire app host, browsable in the
same tree, and **queryable**, because a Parquet file in a bucket is a table that happens to live
somewhere else.

## Connecting

One engine id, `storage`; the scheme picks the provider.

```bash
WDS_CONN_LAKE=s3://bucket/prefix?region=eu-central-1
WDS_CONN_EXPORTS=azblob://account/container
WDS_CONN_ARCHIVE=gs://bucket/2026
WDS_CONN_DROP=file:///data/incoming
```

A prefix in the URL scopes the connection: `s3://bucket/exports/2026` opens at that folder and
nothing above it is reachable.

| Provider | Scheme | Covers |
|---|---|---|
| S3 | `s3://bucket/prefix` | AWS, MinIO, Cloudflare R2, Wasabi, Ceph — anything with an S3 endpoint |
| Azure Blob | `azblob://account/container/prefix` | Azure Blob Storage and Azurite |
| Google Cloud | `gs://bucket/prefix` | Google Cloud Storage |
| Folder | `file:///data/incoming` | a directory in the container or on a mounted volume |

### Credentials

Prefer none. With nothing in the URL the studio uses the identity it runs as — a managed identity on
Azure, an instance role on AWS, application default credentials on Google. A deployment that carries
an access key for its own storage account is a deployment with a secret it did not need.

| Written as | Means |
|---|---|
| nothing | the machine's own identity |
| `?access=…&secret=…` | S3 keys |
| `?endpoint=https://minio:9000` | an S3-compatible endpoint; implies path-style addressing |
| `?region=eu-central-1` | the S3 region |
| `?key=…` | an Azure account key |
| `?sas=…` | an Azure shared-access signature |
| `?connectionstring=…` | an Azure connection string, or the blob service URI — the account name is inside either, so the URL need not repeat it (`azblob:///container?connectionstring=…`) |
| `?credentials=<service account json>` | Google Cloud |
| `?hmac=…&hmacsecret=…` | Google Cloud HMAC keys, which querying needs — see the limit below |

Keys belong in an Aspire parameter or the encrypted connection store, never in a Compose file that
ends up in git.

## Browsing

The tree goes connection → container → prefixes and objects, and **nothing walks a bucket**: a page
is fetched when somebody opens a node and not before, and a folder longer than one page ends in a
row that fetches the next. Each object shows its size and the day it landed.

![The tree, and an object's details](../assets/screenshots/storage-dark.png)

Selecting an object fills the structure panel with what it is: size, content type, last modified,
ETag, storage class, the front of its content as text, an image where it is one, and for anything a
reader understands the columns it would have as a table.

The preview reads the front of a file and never the whole thing — a 4 GB Parquet clicked on by
accident costs a page, not a download. `WDS_STORAGE_PREVIEW_BYTES` sets how much (default 64 kB).

## Querying a file

Double-click a file, or **Open data**, and it opens in the data tab: sorting, the
[filter language](results.md), paging and export all work, because the driver answers "what do I
select from" with a reader over the file instead of a table name.

![A CSV in a bucket, opened as a table](../assets/screenshots/storage-query-dark.png)

| File | Read as |
|---|---|
| `.parquet` | `read_parquet` |
| `.csv`, `.tsv`, `.txt` | `read_csv_auto` |
| `.json`, `.ndjson`, `.jsonl` | `read_json_auto` |
| `.gz`, `.zst`, `.bz2` on top of those | the same reader; DuckDB unpacks it |
| anything else | nothing — the menu offers a preview and a download rather than a query that would fail |

**Query as table…** on a folder asks which of its files belong together (`*.parquet`) and opens the
whole prefix as one table. The pattern is asked for rather than guessed: a folder of CSVs opened as
Parquet would only produce a confusing error.

Everything the studio already does then applies. A masked column is masked in the grid because the
masking looks at column names and never learned where the rows came from. The plan panel shows
DuckDB's plan. `SELECT * FROM read_parquet('s3://…')` in a query tab is an ordinary query, and it
can join a bucket to a database through [federation](federation.md).

The data tab is read-only here, and says so: a file has no key to address a single row by.

## Changing something

Upload, delete and the URI are in the context menu, behind a confirmation.

Both refusals are on the server, not in the UI: a **read-only** connection and one marked as
**production** (red) refuse every upload and delete, the same rule that already refuses to export
unmasked columns from production. Reading is untouched by either.

`WDS_STORAGE_MAX_UPLOAD_BYTES` caps an upload (default 64 MB); anything larger belongs in the
provider's own tool.

Deleting a folder is not offered. It would mean deleting everything under it, which is not a click.

## Offline

DuckDB reads `s3://`, `az://` and `gs://` through its `httpfs` and `azure` extensions, and `INSTALL`
needs the internet — which a container in a private network does not have. The image stages both
extensions at build time with its own DuckDB, so the versions match by construction, and every
session loads them from there with auto-install off. About 60 MB on the image, and the price of
storage working in a closed network.

`WDS_DUCKDB_EXTENSION_DIR` says where they are (`/opt/duckdb/extensions` in the image). Where nothing
is staged — a developer's machine — a session installs them itself.

## From an Aspire app host

```csharp
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var exports = storage.AddBlobs("exports");

builder.AddWebDataStudio("studio")
       .WithBlobStorage(exports)                              // Azurite now, the real account later
       .WithStorage("LAKE", "s3://bucket?region=eu-central-1");
```

`WithBlobStorage` takes the blob resource the app host already models and passes its connection
string through as it is: a connection string for the emulator, the blob service URI once deployed —
where the studio then uses its own managed identity. `WithStorage` covers everything the app host
does not model. Both take `readOnly`, `group` and `color`.

## Limits worth knowing

- **Google Cloud and querying.** DuckDB reaches `gs://` over the S3 protocol, which wants HMAC keys.
  With a service account alone the tree, the preview and the download work; a query does not.
- **Listing and reading cost money.** There is no polling, no prefetching and no background walk.
- **A file is not a table.** No editing, no primary keys, no `UPDATE`.
- **Large objects.** The preview is bounded, the download streams.

## Not in scope

Exporting **to** a bucket, bucket creation, lifecycle rules, policy editing and cross-provider copy.
Each is cheap once this exists, and none of them is why somebody wants to open a bucket.
