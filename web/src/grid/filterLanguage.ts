/// The filter language, evaluated in the browser. The server has the same language in
/// `FilterExpression.cs`, where it becomes SQL; both are checked against
/// `tests/filter-cases.json`, so a filter means the same thing in a query result as in a table
/// browse.
///
///   ^ada  starts with   $son  ends with   +ad  contains   ~ad  does not contain
///   !^ada !$son !=ada    =ada  equals     >10 <=20  compared as a number or a date
///   NULL  NOT NULL  EMPTY  NOT EMPTY
///   TODAY  YESTERDAY  THIS WEEK  LAST MONTH  NEXT YEAR  2026  2026-08  2026-08-23
///   "two words"
///
/// Whitespace is AND, a comma is OR, and OR binds looser.

export type FilterKind = "text" | "number" | "date" | "boolean";

/// A filter box is not a query language: everything past this many terms is ignored.
const MAX_TERMS = 32;

export function filterKindOf(dataType: string | undefined): FilterKind {
  const type = (dataType ?? "").toLowerCase();

  if (type.includes("bool") || type.includes("bit")) return "boolean";
  // "date", "datetime", "timestamp", "timestamptz" — all of them carry one of these two words.
  if (type.includes("date") || type.includes("time")) return "date";
  if (/int|dec|num|real|double|float|money|serial/.test(type)) return "number";

  return "text";
}

/// The two-word terms. Whitespace is AND, so without this "NOT NULL" would read as "contains not"
/// AND "is null".
const TWO_WORDS = new Set([
  "not null", "not empty",
  "this week", "last week", "next week",
  "this month", "last month", "next month",
  "this year", "last year", "next year",
]);

/// Splits on a separator that is not inside double quotes.
function split(text: string, separator: string): string[] {
  const parts: string[] = [];
  let current = "";
  let quoted = false;

  for (const c of text) {
    if (c === '"') { quoted = !quoted; current += c; continue; }
    if (c === separator && !quoted) { if (current) parts.push(current); current = ""; continue; }
    current += c;
  }

  if (current) parts.push(current);
  return parts;
}

function splitAnd(group: string): string[] {
  const words = split(group, " ");
  const terms: string[] = [];

  for (let index = 0; index < words.length; index++) {
    const pair = `${words[index]} ${words[index + 1]}`.toLowerCase();
    // Only a pair that is one of the terms is joined: "not important" stays two words.
    if (index + 1 < words.length && TWO_WORDS.has(pair)) {
      terms.push(`${words[index]} ${words[index + 1]}`);
      index++;
      continue;
    }
    terms.push(words[index]);
  }

  return terms;
}

function unquote(value: string): string {
  return value.length >= 2 && value.startsWith('"') && value.endsWith('"')
    ? value.slice(1, -1).replaceAll('""', '"')
    : value;
}

/// The operators, longest spelling first: "!=" must not be read as "!" then "=".
const OPERATORS = ["!^", "!$", "!=", "<>", "<=", ">=", "^", "$", "+", "~", "=", "<", ">"] as const;

const midnight = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate());

/// The named periods, and the shorthands that are a period rather than a day.
export function period(term: string, now = new Date()): [Date, Date] | null {
  const today = midnight(now);
  const day = (offset: number) =>
    new Date(today.getFullYear(), today.getMonth(), today.getDate() + offset);

  // Monday, because a week that starts on Sunday surprises everybody who is not American.
  const monday = day(-((today.getDay() + 6) % 7));
  const week = (offset: number) =>
    new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + offset);
  const month = (offset: number) => new Date(today.getFullYear(), today.getMonth() + offset, 1);
  const year = (offset: number) => new Date(today.getFullYear() + offset, 0, 1);

  switch (term.toUpperCase().replace("  ", " ")) {
    case "TODAY": return [today, day(1)];
    case "YESTERDAY": return [day(-1), today];
    case "TOMORROW": return [day(1), day(2)];
    case "THIS WEEK": return [monday, week(7)];
    case "LAST WEEK": return [week(-7), monday];
    case "NEXT WEEK": return [week(7), week(14)];
    case "THIS MONTH": return [month(0), month(1)];
    case "LAST MONTH": return [month(-1), month(0)];
    case "NEXT MONTH": return [month(1), month(2)];
    case "THIS YEAR": return [year(0), year(1)];
    case "LAST YEAR": return [year(-1), year(0)];
    case "NEXT YEAR": return [year(1), year(2)];
    default: return shorthand(term);
  }
}

function shorthand(word: string): [Date, Date] | null {
  if (/^\d{4}$/.test(word)) {
    const y = Number(word);
    if (y > 1000 && y < 9999) return [new Date(y, 0, 1), new Date(y + 1, 0, 1)];
  }

  const match = /^(\d{4})-(\d{2})$/.exec(word);
  if (match) {
    const y = Number(match[1]);
    const m = Number(match[2]);
    if (m >= 1 && m <= 12) return [new Date(y, m - 1, 1), new Date(y, m, 1)];
  }

  return null;
}

/// A value as the date it is, or null when it is not one. Strings are read as the server writes
/// them ("2026-08-23 14:30:00"), which `Date` alone treats as UTC in some browsers — so the parts
/// are read out rather than handed over.
function asDate(value: unknown): Date | null {
  if (value instanceof Date) return value;
  if (typeof value === "number") return new Date(value);
  if (typeof value !== "string") return null;

  const match = /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?/.exec(value.trim());
  if (!match) {
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }

  return new Date(
    Number(match[1]), Number(match[2]) - 1, Number(match[3]),
    Number(match[4] ?? 0), Number(match[5] ?? 0), Number(match[6] ?? 0));
}

const asText = (value: unknown) => (value === null || value === undefined ? null : String(value));

/// Does this value survive the filter? An empty filter keeps everything, which is what an empty box
/// means.
export function matchesFilter(value: unknown, kind: FilterKind, filter: string, now = new Date()):
  boolean {
  const groups = split(filter, ",");
  let terms = 0;
  let any = false;

  for (const group of groups) {
    const parts = splitAnd(group);
    let all = true;
    let counted = false;

    for (const part of parts) {
      if (terms >= MAX_TERMS) break;

      const result = term(value, kind, part, now);
      if (result === null) continue; // says nothing

      terms++;
      counted = true;
      if (!result) { all = false; break; }
    }

    if (!counted) continue;
    if (all) any = true;
  }

  // No term said anything at all: nothing was asked, so nothing is filtered out.
  return terms === 0 ? true : any;
}

/// One term: true, false, or null when it says nothing.
function term(value: unknown, kind: FilterKind, raw: string, now: Date): boolean | null {
  const text = raw.trim();
  if (!text) return null;

  const stored = asText(value);
  const lower = stored?.toLowerCase() ?? null;

  switch (text.toUpperCase()) {
    case "NULL": return stored === null;
    case "NOT NULL": return stored !== null;
    case "EMPTY": return stored === null || stored === "";
    case "NOT EMPTY": return stored !== null && stored !== "";
    case "TRUE": return kind === "boolean" ? truthy(value) === true : null;
    case "FALSE": return kind === "boolean" ? truthy(value) === false : null;
  }

  if (kind === "date") {
    const range = period(text, now);
    if (range) {
      const date = asDate(value);
      return date !== null && date >= range[0] && date < range[1];
    }
  }

  for (const token of OPERATORS)
    if (text.startsWith(token)) {
      const rest = unquote(text.slice(token.length).trim());
      if (!rest) return null;

      const needle = rest.toLowerCase();

      switch (token) {
        // A NULL matches no pattern, and "does not contain x" has to hold for a row with no value
        // at all — otherwise the two halves of a filter do not add up to everything.
        case "^": return lower !== null && lower.startsWith(needle);
        case "!^": return lower === null || !lower.startsWith(needle);
        case "$": return lower !== null && lower.endsWith(needle);
        case "!$": return lower === null || !lower.endsWith(needle);
        case "+": return lower !== null && lower.includes(needle);
        case "~": return lower === null || !lower.includes(needle);
        default: return compare(value, kind, token === "<>" ? "!=" : token, rest, lower);
      }
    }

  const bare = unquote(text);

  // Nothing said how to compare, so: text contains, everything else equals.
  return kind === "text"
    ? lower !== null && lower.includes(bare.toLowerCase())
    : compare(value, kind, "=", bare, lower);
}

function truthy(value: unknown): boolean | null {
  if (typeof value === "boolean") return value;
  if (typeof value === "number") return value !== 0;
  if (typeof value === "string") {
    const text = value.trim().toLowerCase();
    if (text === "true" || text === "1" || text === "t") return true;
    if (text === "false" || text === "0" || text === "f") return false;
  }
  return null;
}

const ORDER: Record<string, (a: number) => boolean> = {
  "=": sign => sign === 0,
  "!=": sign => sign !== 0,
  "<": sign => sign < 0,
  "<=": sign => sign <= 0,
  ">": sign => sign > 0,
  ">=": sign => sign >= 0,
};

/// A comparison against a number, a date or a boolean. What cannot be read as one is compared as
/// text, so `>=2026` stays useful on a column the engine calls a string.
function compare(value: unknown, kind: FilterKind, op: string, needle: string, lower: string | null):
  boolean | null {
  const test = ORDER[op];
  if (!test) return null;

  if (kind === "number") {
    const wanted = Number(needle);
    const held = typeof value === "number" ? value : Number(asText(value));
    if (!Number.isNaN(wanted) && !Number.isNaN(held) && asText(value) !== null)
      return test(Math.sign(held - wanted));
  }

  if (kind === "date") {
    const range = shorthand(needle);
    const held = asDate(value);
    if (held === null) return false;

    // A day is a range: "= 2026-08-23" catches every time on that day, and so does "!=" inverted.
    const day = /^\d{4}-\d{2}-\d{2}$/.test(needle.trim()) ? asDate(needle) : null;
    if (day && (op === "=" || op === "!=")) {
      const next = new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1);
      const inside = held >= day && held < next;
      return op === "=" ? inside : !inside;
    }

    const wanted = range ? range[0] : asDate(needle);
    if (wanted) return test(Math.sign(held.getTime() - wanted.getTime()));
  }

  if (kind === "boolean") {
    const wanted = truthy(needle);
    const held = truthy(value);
    if (wanted !== null && held !== null) return test(held === wanted ? 0 : 1);
  }

  if (lower === null) return false;
  const other = needle.toLowerCase();
  return test(lower < other ? -1 : lower > other ? 1 : 0);
}
