# Object storage

A bucket is a place people keep data, and until now the studio could not open one. It can: an
S3-compatible endpoint, Azure Blob Storage, Google Cloud Storage or a plain folder is a connection
like any other — configured through `WDS_CONN_*`, attached from an Aspire app host, browsable in the
same tree, and **queryable**, because a Parquet file in a bucket is a table that happens to live
somewhere else.

## Adding one in the UI

**Connections → Add a bucket** asks for the pieces instead of a URL: the provider, the bucket or
container, an optional prefix, and how to sign in — and it shows the connection it will store, with
every secret masked.

![Adding a bucket](../assets/screenshots/bucket-wizard-dark.png)

The form only offers what a provider has: an endpoint for S3 (which is what MinIO, R2, Wasabi and Ceph
need), an account for Azure, HMAC keys for Google. Changing the provider resets the sign-in choice
rather than carrying a wrong one over, and what is still missing is listed rather than left as a
greyed-out button with no reason.

**Test** reaches the bucket and lists a page before anything is saved: it answers with what is in
there — "reached lake: 3 object(s), 1 folder(s)" — or with what the provider said. Opening a storage
connection alone proves nothing, so a green tick here means the bucket really answered.

`Read-only` and a red colour both refuse every upload and delete afterwards, in the server rather than
in the UI.

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
ETag, storage class, the front of its content as text, and for anything a reader understands the
columns it would have as a table.

**Shown where it lies**, rather than downloaded to be looked at: an image, a PDF, a video, a
recording. A document that arrived as one long line is indented — unless the preview had to stop
part-way, because half a document is not JSON and formatting it would drop what was read.

The preview reads the front of a file and never the whole thing — a 4 GB Parquet clicked on by
accident costs a page, not a download. `WDS_STORAGE_PREVIEW_BYTES` sets how much (default 64 kB). The
things shown in place are the exception: an image or a PDF is served whole, because half of one is
not a picture.

## Taking a file with you

**Download** hands the file to the browser, which decides where it goes.

**Save as…** asks first. Where the browser can (Chromium, Edge), the file is streamed into the place
you picked rather than through memory, so a multi-gigabyte Parquet is a progress bar and not a tab
that dies; elsewhere it falls back to the same download. Both are in the object's context menu and
above its preview.

**Download as zip** and **Save zip as…** are the same two for a whole folder: the prefix is walked
a page at a time and each object is written straight into a zip on the response, so a hundred files
cost a hundred reads and no disk.

A zip has no length before it is written, which is why the limits are counted while it is being
written rather than checked in advance:

| Variable | Meaning |
|---|---|
| `WDS_STORAGE_ARCHIVE_MAX_OBJECTS` | how many objects one zip may hold, default `2000` |
| `WDS_STORAGE_ARCHIVE_MAX_BYTES` | how much it may weigh, default 2 GB |

Whatever stopped the walk is written into the archive as `TRUNCATED.txt`, because a response that is
already streaming cannot go back and become an error. Half an answer that says it is half beats a
file nobody can trust.

The bytes are the provider's own — the studio streams them through and keeps nothing.

**A container that is not there says so in a sentence.** A connection can name one that was never
created — an app host that declared the account and not the container, a name with a typo — and every
provider answers that with a page of XML. The studio reads it for you: *there is no container called
'exports' here*.

## Looking at a file

**View** sits next to *Download* and *Save as…* — on an object in a bucket, in its context menu, and
on a cell that holds a file. It opens the file in the studio instead of in your downloads folder.

The built-in preview already shows what a browser renders by itself: images, PDFs, video, audio and
text. **View** is for the rest of them — a spreadsheet, a Word document, a markdown file, an
archive — through [MudEx](https://www.mudex.org/webcomponents.html)'s file display.

It runs on a page of its own, inside a frame, and that is not an accident. The component puts its
stylesheets — MudBlazor, Roboto and five more — into whatever document loads it, which repaints the
studio white and changes its font; and its WebAssembly runtime refuses to start in a frame without
a real address of its own. A page the studio serves at `/api/viewer/frame` gives it a document to
redecorate and takes the runtime away again when the window closes.

It is fetched the first time somebody asks for it and never before. That has two consequences worth
knowing:

- A studio with no way out to the internet says so instead of showing an empty box, and everything
  else — the preview, the download, *Save as…* — still works.
- `WDS_FILE_VIEWER_URL` points it at your own copy for a deployment that cannot reach a CDN.
  Setting it to nothing switches the viewer off, and the *View* button then says why.

A file in a cell never leaves the browser for this: its bytes become a blob the page reads from
itself, released as soon as the viewer is closed.

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
