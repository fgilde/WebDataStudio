/// The largest file the studio puts into a cell. Hex doubles it on the wire, so eight megabytes of
/// file is a sixteen megabyte request — and a cell editor is not the place to move a video.
export const MAX_BYTES = 8 * 1024 * 1024;

/// Drivers hand binary columns over as `0x…`; this is the same form going back.
export const looksBinary = (value: unknown): value is string =>
  typeof value === "string" && /^0x([0-9a-f]{2})*$/i.test(value) && value.length > 2;

export const toBytes = (hex: string): Uint8Array => {
  const digits = hex.slice(2);
  const bytes = new Uint8Array(digits.length / 2);

  for (let i = 0; i < bytes.length; i++) bytes[i] = parseInt(digits.slice(i * 2, i * 2 + 2), 16);

  return bytes;
};

export const toHex = (bytes: Uint8Array): string =>
  "0x" + Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");

/// What a blob actually is, read from its first bytes rather than from a column name.
///
/// The point is the download: a PDF saved as `column.txt` is a file nobody can open, and the column
/// name never says what is in it. The list is short on purpose — these are what ends up in a
/// database column, and anything else is honestly called `.bin`.
export function sniff(bytes: Uint8Array): { extension: string; mime: string } {
  const head = Array.from(bytes.slice(0, 12), b => b.toString(16).padStart(2, "0")).join("").toUpperCase();

  if (head.startsWith("89504E47")) return { extension: "png", mime: "image/png" };
  if (head.startsWith("FFD8FF")) return { extension: "jpg", mime: "image/jpeg" };
  if (head.startsWith("47494638")) return { extension: "gif", mime: "image/gif" };
  if (head.startsWith("25504446")) return { extension: "pdf", mime: "application/pdf" };
  if (head.startsWith("1F8B")) return { extension: "gz", mime: "application/gzip" };
  if (head.startsWith("377ABCAF")) return { extension: "7z", mime: "application/x-7z-compressed" };
  if (head.startsWith("52494646") && head.slice(16, 24) === "57454250")
    return { extension: "webp", mime: "image/webp" };

  // A zip is also every office format; without the central directory there is no telling which.
  if (head.startsWith("504B0304")) return { extension: "zip", mime: "application/zip" };
  if (head.slice(8, 16) === "66747970") return { extension: "mp4", mime: "video/mp4" };

  return { extension: "bin", mime: "application/octet-stream" };
}

/// A file name for the download: the column, and what the bytes say they are.
export const fileNameFor = (column: string, bytes: Uint8Array): string =>
  `${column.replace(/[^\w.-]+/g, "_")}.${sniff(bytes).extension}`;

/// How big it is, in the words somebody would use.
export function size(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} kB`;

  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/// Saves whatever is in a cell as a file: bytes as themselves, anything else as text.
export function saveCell(column: string, value: unknown) {
  const binary = looksBinary(value);
  const bytes = binary ? toBytes(value) : new TextEncoder().encode(String(value ?? ""));
  const { mime } = binary ? sniff(bytes) : { mime: "text/plain" };
  const name = binary ? fileNameFor(column, bytes) : `${column.replace(/[^\w.-]+/g, "_")}.txt`;

  const url = URL.createObjectURL(new Blob([bytes as BlobPart], { type: mime }));
  const anchor = document.createElement("a");

  anchor.href = url;
  anchor.download = name;
  anchor.click();
  URL.revokeObjectURL(url);
}

/// A file picked from disk, as the value a binary cell takes. Rejects one too large to send rather
/// than letting the browser run out of memory building the hex for it.
export async function readFileAsCell(file: File): Promise<string> {
  if (file.size > MAX_BYTES)
    throw new Error(`${file.name} is ${size(file.size)}; a cell takes up to ${size(MAX_BYTES)}`);

  return toHex(new Uint8Array(await file.arrayBuffer()));
}
