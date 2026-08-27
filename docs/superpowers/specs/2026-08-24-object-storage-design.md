# Object storage as a connection

A bucket is a place people keep data, and the studio cannot open one. This adds object storage —
S3-compatible, Azure Blob, Google Cloud Storage, and a plain folder — as a connection like any other:
configured through `WDS_CONN_*`, attached from an Aspire app host, browsable in the same tree, and
**queryable**, because a Parquet file in a bucket is a table that happens to live somewhere else.

## What it is for

Three things people currently leave the studio to do:

1. **Look.** What is in `exports/2026/08`, how big is it, when did it land, what does the first page
   of it say.
2. **Ask.** How many rows are in yesterday's dump, and do they join with what is in the database.
3. **Change, carefully.** Drop a file in, take a broken one out — with the same "read it before it
   runs" rule the rest of the studio applies to a `DELETE`.

## What it is not

- Not a sync tool, a lifecycle manager, or a permissions editor. No bucket creation, no policy
  editing, no versioning UI.
- Not an ETL runner. The studio queries files where they are; moving data between systems stays the
  job of the tools that own that.
- Not a replacement for a storage explorer when somebody needs to manage a storage account. It is
  the data-shaped half of that.

## The shape: one connection with two faces

The storage driver implements `IDbDriver` like every other driver and, inside, holds a **DuckDB**
session with the connection's credentials registered as DuckDB secrets.

| The driver is asked | It answers with |
|---|---|
| `IntrospectAsync` | containers, then prefixes and objects, paged by continuation token |
| `DescribeAsync` on an object | size, content type, last modified, ETag, storage class, and the column list for Parquet |
| `ExecuteAsync` | DuckDB, against the bucket |
| `ExplainAsync` | DuckDB's plan, as for any DuckDB connection |

Everything the studio already does then applies without new plumbing: the query tab, the plan panel,
export, charts, the map, archives, the filter language, and masking. A Parquet column called
`api_token` is masked in the grid because the masking looks at column names, and it never learned
where the rows came from.

### The one change in the core

A driver may say how an object reference becomes a `FROM` clause:

```csharp
public interface IDbDriver
{
    /// What to select from for this object. A database returns the qualified name; object storage
    /// returns a reader over the file, which is the same thing said differently.
    string FromClause(SchemaNodeRef target) => /* default: the qualified name */;
}
```

- A table: `"public"."orders"` — exactly what `ChangeScriptBuilder.Qualify` produces today.
- An object: `read_parquet('az://exports/2026/08/orders.parquet')`
- A prefix: `read_parquet('az://exports/2026/08/*.parquet')` — a folder is one table, verified.

`DataEndpoints` calls this instead of qualifying the name itself, and **Open data on a file works**,
with sorting, the filter language, paging and export, for the price of one interface member with a
default implementation.

Reading is decided by extension and content type: `.parquet` → `read_parquet`, `.csv`/`.tsv`/`.txt` →
`read_csv` with `AUTO_DETECT`, `.json`/`.ndjson` → `read_json_auto`. Anything else has no `FROM`, and
the menu offers preview and download rather than a query that would fail.

## Providers

One interface, four implementations. Nothing above it knows which one it is talking to.

```csharp
public interface IObjectStore
{
    Task<StoragePage> ListAsync(string prefix, string? cursor, int max, CancellationToken ct);
    Task<StorageObject?> HeadAsync(string key, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task WriteAsync(string key, Stream content, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);

    /// The URI DuckDB reads this object by: az://, s3://, gs://, or a local path.
    string SqlUri(string key);

    /// The CREATE SECRET this store needs before DuckDB can read it.
    string? SecretStatement();
}
```

| Provider | SDK | Covers |
|---|---|---|
| `s3://` | `AWSSDK.S3` | AWS, MinIO, Cloudflare R2, Wasabi, Ceph — anything with an S3 endpoint |
| `azblob://` | `Azure.Storage.Blobs` | Azure Blob Storage, Azurite |
| `gs://` | `Google.Cloud.Storage.V1` | Google Cloud Storage |
| `file://` | `System.IO` | a folder on the machine or a mounted volume |

A `StoragePage` carries entries and the cursor for the next one. **Nothing walks a bucket**: the
schema cache that makes the object tree instant for a database is exactly the wrong idea for a
container with a million objects, so listing is lazy, paged, and shows a "load more" row.

## Configuration

One engine id, `storage`; the scheme picks the provider.

```bash
WDS_CONN_LAKE=s3://bucket/prefix?region=eu-central-1
WDS_CONN_EXPORTS=azblob://account/container
WDS_CONN_ARCHIVE=gs://bucket/2026
WDS_CONN_DROP=file:///data/incoming
```

Credentials, in both directions:

| Written as | Means |
|---|---|
| no credentials in the URL | the machine's own identity: managed identity on Azure, an instance role on AWS, ADC on Google |
| `?key=…` (Azure), `?access=…&secret=…` (S3), `?hmac=…` (GCS) | explicit, stored encrypted like every other connection secret |
| `?sas=…` | an Azure shared-access signature |
| `?endpoint=…` | an S3-compatible endpoint; implies path-style addressing |

Managed identity from the start, not after: the studio already signs in to Azure SQL that way
(F16.1), and a deployment that has to carry an access key for its own storage account is a
deployment with a secret it did not need.

`readOnly` on the connection blocks every write. A connection marked as production (colour red)
refuses writes outright, the same rule that already refuses to export unmasked columns from one.

## DuckDB, offline

DuckDB reads `s3://`, `az://` and `gs://` through its `httpfs` and `azure` extensions, and
`INSTALL` needs the internet — which a container in a private network does not have.

Verified mitigation (measured, not assumed):

1. The image build downloads the two extensions for its platform into
   `/opt/duckdb/extensions/v<version>/<platform>/`.
2. Every storage session opens with

   ```sql
   SET extension_directory='/opt/duckdb/extensions';
   SET autoinstall_known_extensions=false;
   SET autoload_known_extensions=false;
   LOAD httpfs; LOAD azure;
   ```

3. `duckdb_extensions()` then reports both loaded from the bundle, with no network call.

Cost: about 58 MB on the image for the two files. `gs://` needs no third extension — httpfs serves
it, with HMAC keys.

## What the person sees

**Tree.** The connection, then containers, then prefixes and objects. Three new node kinds
(`Container`, `Prefix`, `StorageObject`) with their own icons. Objects show size and modified date in
the row.

**Detail panel.** A Preview tab — text, JSON, CSV as a grid, an image, and for Parquet the column
list with types and the row count from the footer — beside size, content type, last modified, ETag
and storage class.

**Context menu.** Open data · Query as table… · Download · Upload here… · Delete… · Copy the URI.

**The data tab** on an object is read-only, and says why: a file has no key to address a single row
by. The reason mechanism for that already exists.

## Aspire

```csharp
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var exports = storage.AddBlobs("exports");

builder.AddWebDataStudio("studio")
       .WithBlobStorage(exports)                      // WDS_CONN_EXPORTS, from the resource
       .WithStorage("LAKE", "s3://bucket?region=eu-central-1");
```

`WithBlobStorage` takes the app host's blob resource — Azurite while developing, the real account
when deployed — and writes the connection the same way `WithReference` does for a database.
`WithStorage(name, url)` covers everything the app host does not model.

## Testing

| What | How |
|---|---|
| The four providers | one contract suite, run against MinIO and Azurite (Testcontainers), `fake-gcs-server`, and a temp folder |
| URI mapping and reader choice | pure tests: key in, `read_parquet('s3://…')` out, and no `FROM` for a `.zip` |
| Secret statements | asserted as text, per provider, with no network |
| Offline extensions | a test that loads `httpfs` from a staged directory with auto-install off |
| End to end | write a Parquet into MinIO, browse to it, open its data, filter it, export it |
| Guarded writes | upload and delete refused on a read-only connection and on a production one |

## Risks, and what is done about them

1. **Extensions offline** — solved above, measured. The image gets bigger; that is the price.
2. **GCS through DuckDB wants HMAC keys.** With a service account only, browsing works and querying
   does not. This goes in the engine matrix as a stated limit, not as a surprise.
3. **Listing and reading cost money.** No polling, no prefetching, no background walk. A page is
   fetched when somebody opens a node, and not before.
4. **A file is not a table.** No editing, no primary keys, no `UPDATE`. The data tab says so rather
   than offering a Save button that cannot work.
5. **Large objects.** Preview reads a bounded prefix of the stream, never the whole file; download
   streams.

## The features this becomes

Written into `docs/features.md` and the spec's own table as F28, so the coverage test can hold them:

| Id | What |
|---|---|
| F28.1 | Object storage as a connection: S3-compatible, Azure Blob, Google Cloud Storage, a folder |
| F28.2 | Containers, prefixes and objects in the tree, paged rather than walked |
| F28.3 | An object's details and a preview: text, JSON, CSV, an image, a Parquet schema |
| F28.4 | A file or a whole prefix queried as a table, through DuckDB, with the studio's own grid |
| F28.5 | Upload, delete and copy behind a confirmation, refused on a read-only or production connection |
| F28.6 | The machine's own identity as credentials, or explicit keys stored encrypted |
| F28.7 | The storage extensions bundled into the image, so a private network needs no download |
| F28.8 | A storage connection attached from an Aspire app host |

## Out of scope, deliberately

Export **to** a bucket, archives in a bucket, bucket creation, lifecycle rules, and cross-provider
copy. Each is cheap once this exists, and none of them is why somebody wants to open a bucket.
