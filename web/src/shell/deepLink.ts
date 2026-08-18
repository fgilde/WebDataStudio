export type DeepLink =
  | { kind: "object"; connectionId: string; objectRef: string }
  | { kind: "query"; connectionId: string; savedQueryId: string };

/// Links look like `#/c/{connectionId}/o/{objectRef}` — the object reference itself contains
/// slashes, so it travels encoded.
export function buildDeepLink(target: DeepLink): string {
  const connection = encodeURIComponent(target.connectionId);

  return target.kind === "object"
    ? `#/c/${connection}/o/${encodeURIComponent(target.objectRef)}`
    : `#/c/${connection}/q/${encodeURIComponent(target.savedQueryId)}`;
}

export function parseDeepLink(url: string): DeepLink | null {
  const hash = url.includes("#") ? url.slice(url.indexOf("#")) : url;
  const parts = hash.replace(/^#\/?/, "").split("/");

  if (parts.length !== 4 || parts[0] !== "c") return null;

  const connectionId = decodeURIComponent(parts[1]);
  const value = decodeURIComponent(parts[3]);
  if (!connectionId || !value) return null;

  if (parts[2] === "o") return { kind: "object", connectionId, objectRef: value };
  if (parts[2] === "q") return { kind: "query", connectionId, savedQueryId: value };
  return null;
}
