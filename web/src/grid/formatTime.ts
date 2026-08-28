/// Which clock a timestamp is shown on.
///
/// `local` is the browser's own zone, `utc` is UTC, and anything else is an IANA name the browser
/// knows (`Europe/Berlin`). It only ever changes what is *shown*: nothing is rewritten on the way
/// into the database, where a value keeps the zone — or the absence of one — that its column has.
export type TimeZoneSetting = string;

/// A timestamp as it arrives: ISO 8601, because that is what the drivers write (`ToString("O")`).
/// The last group is what matters — a `Z` or an offset means the value knows its zone, and nothing
/// else does.
const ISO = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:?\d{2})?$/;

export interface ParsedTime {
  /// What to show.
  text: string;
  /// Whether the value carried a zone. One that does not is never converted: "14:00" in a
  /// `timestamp without time zone` means 14:00, and turning it into 16:00 would be an invention.
  zoned: boolean;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

/// Cached: a formatter costs more to build than to use, and a grid builds one per cell otherwise.
function formatter(zone: string): Intl.DateTimeFormat {
  const existing = formatters.get(zone);
  if (existing) return existing;

  const made = new Intl.DateTimeFormat("sv-SE", {
    timeZone: zone === "local" ? undefined : zone,
    year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", second: "2-digit",
    hour12: false,
  });

  formatters.set(zone, made);
  return made;
}

/// True for the shape a timestamp arrives in.
export const looksTimestamp = (value: unknown): value is string =>
  typeof value === "string" && ISO.test(value);

/// One timestamp, on the clock somebody chose.
///
/// Seven decimal places and a `T` in the middle are what the wire format looks like, not what a
/// person reads. Fractions are kept only when they are not zeros, because "12:00:00.0000000" says
/// nothing that "12:00:00" does not.
export function formatTimestamp(value: string, zone: TimeZoneSetting = "local"): ParsedTime {
  const match = ISO.exec(value);
  if (!match) return { text: value, zoned: false };

  const [, year, month, day, hour, minute, second, fraction, suffix] = match;
  const trimmed = (fraction ?? "").replace(/0+$/, "");

  // Without a zone there is nothing to convert to: the value is shown as it is stored, tidied up.
  if (!suffix)
    return {
      text: `${year}-${month}-${day} ${hour}:${minute}:${second}${trimmed ? `.${trimmed}` : ""}`,
      zoned: false,
    };

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return { text: value, zoned: true };

  try {
    const shown = formatter(zone).format(date).replace(",", "");
    return { text: `${shown}${trimmed ? `.${trimmed}` : ""}`, zoned: true };
  } catch {
    // An IANA name this browser does not know: better the raw value than an exception per cell.
    return { text: value, zoned: true };
  }
}

/// What the footer says about the clock, or null when it is the one the reader is on anyway.
export function describeZone(zone: TimeZoneSetting): string | null {
  if (zone === "local") return null;

  return zone === "utc" ? "times in UTC" : `times in ${zone}`;
}

/// Whether a column's declared type keeps a zone. The answer belongs next to the value: the same
/// "14:00" means two different moments in `timestamptz` and in `timestamp`.
export function carriesZone(dataType: string): boolean | null {
  const type = dataType.toLowerCase();

  if (/timestamptz|with time zone|datetimeoffset|timestamp_tz/.test(type)) return true;
  if (/timestamp|datetime|^date$/.test(type)) return false;

  return null;
}
