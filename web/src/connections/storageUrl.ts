/// What somebody fills in for a bucket, and the URL that comes out of it.
///
/// The studio's storage connections are URLs, which is right for a `WDS_CONN_*` in a Compose file and
/// wrong as a thing to type by hand: nobody remembers whether the account goes in the host or the
/// path, or which of `?key=`, `?sas=` and `?connectionstring=` Azure wants. So the wizard collects
/// fields and this builds the URL — and it lives on its own, without React, because the escaping is
/// the part that has to be right.

export type StorageProvider = "s3" | "azblob" | "gs" | "file";

/// How the connection proves who it is. `identity` means nothing is written down: the managed
/// identity, the instance role, or application default credentials.
export type StorageAuth = "identity" | "keys" | "sas" | "connectionstring" | "json";

export interface StorageDraft {
  provider: StorageProvider;
  /// The bucket for S3 and Google, the container for Azure, the path for a folder.
  container: string;
  /// The storage account. Azure only, and not needed when a connection string carries it.
  account: string;
  /// Opens the connection inside the container rather than at its root.
  prefix: string;
  region: string;
  /// An S3-compatible endpoint: MinIO, R2, Wasabi, Ceph.
  endpoint: string;
  auth: StorageAuth;
  /// S3: the access key. Azure: unused. Google: unused.
  access: string;
  /// S3: the secret key. Azure: the account key. The one field that is a secret in every provider.
  secret: string;
  /// An Azure shared-access signature, or an Azure connection string, or a Google service-account
  /// JSON — whichever `auth` names.
  token: string;
  /// Google HMAC keys, which querying through DuckDB needs and browsing does not.
  hmac: string;
  hmacSecret: string;
}

export const emptyDraft: StorageDraft = {
  provider: "s3", container: "", account: "", prefix: "", region: "", endpoint: "",
  auth: "identity", access: "", secret: "", token: "", hmac: "", hmacSecret: "",
};

/// Which auth choices a provider actually has. Offering an Azure SAS for an S3 bucket would only
/// invite the question.
export function authChoices(provider: StorageProvider): { value: StorageAuth; label: string }[] {
  switch (provider) {
    case "s3":
      return [
        { value: "identity", label: "The machine's own role" },
        { value: "keys", label: "Access key and secret" },
      ];
    case "azblob":
      return [
        { value: "identity", label: "Managed identity" },
        { value: "keys", label: "Account key" },
        { value: "sas", label: "Shared-access signature" },
        { value: "connectionstring", label: "Connection string" },
      ];
    case "gs":
      return [
        { value: "identity", label: "Application default credentials" },
        { value: "json", label: "Service-account JSON" },
      ];
    case "file":
      return [{ value: "identity", label: "The container's own filesystem" }];
  }
}

/// What is missing or wrong, in the order somebody would fix it. Empty means the draft builds.
export function storageProblems(draft: StorageDraft): string[] {
  const problems: string[] = [];

  if (draft.provider === "file") {
    if (!draft.container.trim()) problems.push("a path inside the container is needed");
    return problems;
  }

  if (!draft.container.trim())
    problems.push(draft.provider === "azblob" ? "a container is needed" : "a bucket is needed");

  if (draft.provider === "azblob" && draft.auth !== "connectionstring" && !draft.account.trim())
    problems.push("a storage account is needed");

  if (draft.auth === "keys") {
    if (draft.provider === "s3" && !draft.access.trim()) problems.push("an access key is needed");
    if (!draft.secret.trim())
      problems.push(draft.provider === "s3" ? "a secret key is needed" : "an account key is needed");
  }

  if (draft.auth === "sas" && !draft.token.trim()) problems.push("a shared-access signature is needed");

  if (draft.auth === "connectionstring" && !draft.token.trim())
    problems.push("a connection string is needed");

  if (draft.auth === "json" && !draft.token.trim())
    problems.push("the service-account JSON is needed");

  if (draft.endpoint.trim() && !/^https?:\/\//i.test(draft.endpoint.trim()))
    problems.push("an endpoint starts with http:// or https://");

  // One HMAC half without the other is a query that fails later rather than a mistake caught now.
  if ((draft.hmac.trim() === "") !== (draft.hmacSecret.trim() === ""))
    problems.push("HMAC keys come in pairs: both the key and its secret");

  return problems;
}

/// The URL the studio stores. Secrets travel as escaped query values, which is what keeps a base64
/// key with a `+` in it intact.
export function buildStorageUrl(draft: StorageDraft): string {
  const prefix = draft.prefix.trim().replace(/^\/+|\/+$/g, "");
  const container = draft.container.trim().replace(/^\/+|\/+$/g, "");

  if (draft.provider === "file") {
    // file:///data/incoming — three slashes, and the path is the whole of it.
    const path = draft.container.trim().replace(/\\/g, "/").replace(/^\/+/, "");
    return `file:///${path}${prefix ? `/${prefix}` : ""}`;
  }

  const options: [string, string][] = [];

  if (draft.provider === "s3") {
    if (draft.region.trim()) options.push(["region", draft.region.trim()]);
    if (draft.endpoint.trim()) options.push(["endpoint", draft.endpoint.trim()]);
    if (draft.auth === "keys") {
      options.push(["access", draft.access.trim()]);
      options.push(["secret", draft.secret]);
    }
  }

  if (draft.provider === "azblob") {
    if (draft.endpoint.trim()) options.push(["endpoint", draft.endpoint.trim()]);
    if (draft.auth === "keys") options.push(["key", draft.secret]);
    if (draft.auth === "sas") options.push(["sas", draft.token.replace(/^\?/, "")]);
    if (draft.auth === "connectionstring") options.push(["connectionstring", draft.token.trim()]);
  }

  if (draft.provider === "gs") {
    if (draft.auth === "json") options.push(["credentials", draft.token.trim()]);
    if (draft.hmac.trim()) {
      options.push(["hmac", draft.hmac.trim()]);
      options.push(["hmacsecret", draft.hmacSecret]);
    }
  }

  const query = options.length === 0
    ? ""
    : "?" + options.map(([key, value]) => `${key}=${encodeURIComponent(value)}`).join("&");

  const path = [container, prefix].filter(Boolean).join("/");

  // Azure puts the account in the host and the container in the path; the others put the bucket in
  // the host. An Azure connection string already names the account, so the host may stay empty.
  if (draft.provider === "azblob")
    return `azblob://${draft.account.trim()}/${path}${query}`;

  return `${draft.provider}://${path}${query}`;
}

/// The same URL with every secret replaced, for showing on screen. A wizard that prints an account
/// key in a preview is a wizard that puts it in a screenshot.
export function maskStorageUrl(url: string): string {
  return url.replace(
    /([?&](?:secret|key|sas|connectionstring|credentials|hmacsecret)=)([^&]*)/gi,
    (_, head: string) => `${head}…`);
}

/// A name somebody does not have to invent: LAKE from `s3://lake`, EXPORTS from a container.
export function suggestName(draft: StorageDraft): string {
  const source = draft.provider === "file"
    ? draft.container.trim().replace(/\\/g, "/").split("/").filter(Boolean).pop() ?? ""
    : draft.container.trim();

  return source.replace(/[^A-Za-z0-9]+/g, "_").replace(/^_+|_+$/g, "").toUpperCase();
}
