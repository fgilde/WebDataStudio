import { describe, expect, it } from "vitest";
import {
  authChoices, buildStorageUrl, emptyDraft, maskStorageUrl, storageProblems, suggestName,
  type StorageDraft,
} from "./storageUrl";

const draft = (patch: Partial<StorageDraft>): StorageDraft => ({ ...emptyDraft, ...patch });

describe("buildStorageUrl", () => {
  it("puts an S3 bucket in the host and the prefix in the path", () => {
    expect(buildStorageUrl(draft({
      provider: "s3", container: "lake", prefix: "exports/2026", region: "eu-central-1",
    }))).toBe("s3://lake/exports/2026?region=eu-central-1");
  });

  it("adds keys where they were given and nothing where they were not", () => {
    expect(buildStorageUrl(draft({
      provider: "s3", container: "lake", auth: "keys", access: "AKIA", secret: "s3cr3t",
    }))).toBe("s3://lake?access=AKIA&secret=s3cr3t");

    // The machine's own role: no secret in the URL at all, which is the point of it.
    expect(buildStorageUrl(draft({ provider: "s3", container: "lake" }))).toBe("s3://lake");
  });

  it("keeps a base64 secret intact", () => {
    // A '+' in a query value is a space to a query parser; escaping it is what makes the key arrive.
    const url = buildStorageUrl(draft({
      provider: "s3", container: "lake", auth: "keys", access: "AKIA", secret: "ab+cd/ef==",
    }));

    expect(url).toContain("secret=ab%2Bcd%2Fef%3D%3D");
    expect(new URLSearchParams(url.split("?")[1]).get("secret")).toBe("ab+cd/ef==");
  });

  it("puts an Azure account in the host and the container in the path", () => {
    expect(buildStorageUrl(draft({
      provider: "azblob", account: "acct", container: "exports", auth: "keys", secret: "k+/=",
    }))).toBe("azblob://acct/exports?key=k%2B%2F%3D");
  });

  it("lets an Azure connection string carry the account on its own", () => {
    const url = buildStorageUrl(draft({
      provider: "azblob", container: "exports", auth: "connectionstring",
      token: "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=k+/=",
    }));

    // azblob:///container — the server reads the account out of the connection string.
    expect(url.startsWith("azblob:///exports?connectionstring=")).toBe(true);
  });

  it("strips the leading question mark of a shared-access signature", () => {
    expect(buildStorageUrl(draft({
      provider: "azblob", account: "acct", container: "exports", auth: "sas", token: "?sv=2024&sig=x",
    }))).toBe("azblob://acct/exports?sas=sv%3D2024%26sig%3Dx");
  });

  it("carries Google credentials and, where given, the HMAC keys a query needs", () => {
    const url = buildStorageUrl(draft({
      provider: "gs", container: "lake", auth: "json", token: '{"type":"service_account"}',
      hmac: "GOOG1E", hmacSecret: "abc+/",
    }));

    expect(url.startsWith("gs://lake?credentials=")).toBe(true);
    expect(url).toContain("hmac=GOOG1E");
    expect(url).toContain("hmacsecret=abc%2B%2F");
  });

  it("writes a folder as a three-slash file URL", () => {
    expect(buildStorageUrl(draft({ provider: "file", container: "/data/incoming" })))
      .toBe("file:///data/incoming");

    // A Windows path typed into the box is still a path.
    expect(buildStorageUrl(draft({ provider: "file", container: "C:\\data\\incoming" })))
      .toBe("file:///C:/data/incoming");
  });

  it("does not care about stray slashes", () => {
    expect(buildStorageUrl(draft({ provider: "s3", container: "/lake/", prefix: "/exports/" })))
      .toBe("s3://lake/exports");
  });
});

describe("storageProblems", () => {
  it("names what is missing rather than refusing silently", () => {
    expect(storageProblems(draft({ provider: "s3" }))).toEqual(["a bucket is needed"]);
    expect(storageProblems(draft({ provider: "azblob", container: "exports" })))
      .toEqual(["a storage account is needed"]);
    expect(storageProblems(draft({ provider: "file" })))
      .toEqual(["a path inside the container is needed"]);
  });

  it("asks for both halves of a key pair", () => {
    expect(storageProblems(draft({ provider: "s3", container: "lake", auth: "keys" })))
      .toEqual(["an access key is needed", "a secret key is needed"]);
  });

  it("asks for both halves of the HMAC pair, because one alone fails later", () => {
    expect(storageProblems(draft({ provider: "gs", container: "lake", hmac: "GOOG1E" })))
      .toContain("HMAC keys come in pairs: both the key and its secret");
  });

  it("wants an endpoint that is a URL", () => {
    expect(storageProblems(draft({ provider: "s3", container: "lake", endpoint: "minio:9000" })))
      .toContain("an endpoint starts with http:// or https://");
    expect(storageProblems(draft({
      provider: "s3", container: "lake", endpoint: "http://minio:9000",
    }))).toEqual([]);
  });

  it("is happy with the machine's own identity and nothing else", () => {
    expect(storageProblems(draft({ provider: "s3", container: "lake" }))).toEqual([]);
    expect(storageProblems(draft({ provider: "azblob", account: "acct", container: "exports" })))
      .toEqual([]);
    expect(storageProblems(draft({ provider: "gs", container: "lake" }))).toEqual([]);
  });
});

describe("authChoices", () => {
  it("offers each provider only what it has", () => {
    expect(authChoices("s3").map(choice => choice.value)).toEqual(["identity", "keys"]);
    expect(authChoices("azblob").map(choice => choice.value))
      .toEqual(["identity", "keys", "sas", "connectionstring"]);
    expect(authChoices("gs").map(choice => choice.value)).toEqual(["identity", "json"]);
    expect(authChoices("file")).toHaveLength(1);
  });
});

describe("maskStorageUrl", () => {
  it("hides every secret and keeps everything a person needs to read", () => {
    const masked = maskStorageUrl(
      "s3://lake?region=eu-central-1&access=AKIA&secret=s3cr3t");

    expect(masked).toBe("s3://lake?region=eu-central-1&access=AKIA&secret=…");
  });

  it("hides an Azure key, a SAS, a connection string and Google credentials", () => {
    for (const [option, value] of [
      ["key", "k+/="], ["sas", "sv%3D2024"], ["connectionstring", "AccountKey%3Dx"],
      ["credentials", "%7B%7D"], ["hmacsecret", "abc"],
    ])
      expect(maskStorageUrl(`azblob://acct/exports?${option}=${value}`))
        .toBe(`azblob://acct/exports?${option}=…`);
  });
});

describe("suggestName", () => {
  it("proposes a name nobody has to invent", () => {
    expect(suggestName(draft({ provider: "s3", container: "data-lake" }))).toBe("DATA_LAKE");
    expect(suggestName(draft({ provider: "file", container: "/data/incoming" }))).toBe("INCOMING");
    expect(suggestName(draft({ provider: "s3", container: "" }))).toBe("");
  });
});
