/// Subsequence matching, the way an IDE's go-to-file works: "ordit" finds "order_items". Used by
/// the explorer's filter and by "go to object", which had a copy of this each.
export const fuzzyMatches = (needle: string, candidate: string): boolean => {
  const target = candidate.toLowerCase();
  let index = 0;

  for (const character of needle.toLowerCase()) {
    index = target.indexOf(character, index);
    if (index < 0) return false;
    index++;
  }

  return true;
};

/// Lower is better. A prefix beats a word start, a word start beats a scattered match, and a short
/// name beats a long one — otherwise "order" ranks "reordering_log" above "orders".
export function fuzzyScore(needle: string, candidate: string): number {
  const query = needle.toLowerCase();
  const target = candidate.toLowerCase();

  if (!fuzzyMatches(query, target)) return Number.POSITIVE_INFINITY;

  const exact = target === query ? 0 : 100;
  const position = target.indexOf(query);

  const contiguous = position === 0
    ? 0                                        // starts with it
    : position > 0
      ? /[^a-z0-9]/.test(target[position - 1] ?? "") ? 10 : 20   // at a word boundary, or inside
      : 40;                                    // only as a subsequence

  return exact + contiguous + target.length / 100;
}

/// The best matches first, cut to `limit`. Anything that does not match at all is left out.
export function fuzzyRank<T>(items: T[], needle: string, of: (item: T) => string, limit = 200): T[] {
  const scored = items
    .map(item => ({ item, score: fuzzyScore(needle, of(item)) }))
    .filter(entry => Number.isFinite(entry.score))
    .sort((a, b) => a.score - b.score);

  return scored.slice(0, limit).map(entry => entry.item);
}
