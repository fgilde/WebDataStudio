import { useEffect, useState } from "react";
import { ActionIcon, Group, NumberInput, Pagination, Select, Text, Tooltip } from "@mantine/core";
import { IconSum } from "@tabler/icons-react";

/// The bar under a grid: which rows these are, how to get to others, and how many to show.
///
/// The old one was a row of page numbers and nothing else, which answers "where am I" only if you
/// multiply in your head, and answers "how far is it to the end" not at all. Four things were
/// missing and they are all here: the range in rows, the ends of the table, a way to type a page
/// number rather than clicking towards it, and the page size — the one setting whose right value
/// depends on the table in front of you rather than on a preference set once.
export interface PagerProps {
  /// Which page is shown, counting from one.
  page: number;
  pageSize: number;
  /// How many rows this page actually holds — the last page is usually short.
  rowsOnPage: number;
  /// What the server said the table holds, or null where it could not say.
  total: number | null;
  /// Whether that total is the catalogue's guess rather than a count.
  totalIsEstimate?: boolean;
  /// Whether a filter narrows this result, which makes a table-wide total the wrong number.
  filtered?: boolean;
  onPage: (page: number) => void;
  onPageSize: (size: number) => void;
  /// Counts the rows for real. Absent where the engine cannot.
  onCount?: () => void;
  counting?: boolean;
}

const SIZES = ["25", "50", "100", "200", "500", "1000", "5000"];

/// "1–200 of 12,345", and what to say when one of those numbers is not known.
export function rangeLabel(
  page: number, pageSize: number, rowsOnPage: number,
  total: number | null, totalIsEstimate?: boolean, filtered?: boolean): string {
  if (rowsOnPage === 0) return "no rows";

  const first = (page - 1) * pageSize + 1;
  const last = first + rowsOnPage - 1;
  const range = `${first.toLocaleString()}–${last.toLocaleString()}`;

  // A filter makes the table's own total the answer to a different question, so it is not offered
  // as this result's size. Counting says what this result holds.
  if (filtered || total === null) return `${range} of ?`;

  return `${range} of ${totalIsEstimate ? "≈" : ""}${total.toLocaleString()}`;
}

export function pageCount(pageSize: number, total: number | null): number {
  if (!total || total <= 0) return 1;
  return Math.max(1, Math.ceil(total / pageSize));
}

export function Pager({
  page, pageSize, rowsOnPage, total, totalIsEstimate, filtered,
  onPage, onPageSize, onCount, counting,
}: PagerProps) {
  const pages = pageCount(pageSize, total);
  const [jump, setJump] = useState<string | number>(page);

  // The box follows the buttons: clicking "next" and then typing into it should start from where
  // the grid actually is.
  useEffect(() => setJump(page), [page]);

  // An unknown or filtered total makes the last page unknowable, so paging goes on as long as the
  // page in front of you is full — one more than the current page, never a wrong end.
  const known = total !== null && !filtered;
  const reachable = known ? pages : page + (rowsOnPage < pageSize ? 0 : 1);

  const goTo = () => {
    const wanted = Math.trunc(Number(jump));
    if (!Number.isFinite(wanted) || wanted < 1) return setJump(page);
    onPage(Math.min(wanted, reachable));
  };

  const counted = (
    <Text size="xs" c="dimmed" data-testid="pager-range">
      {rangeLabel(page, pageSize, rowsOnPage, total, totalIsEstimate, filtered)}
    </Text>
  );

  return (
    <Group justify="space-between" gap="xs" py={4} px="xs" wrap="nowrap">
      <Group gap={6} wrap="nowrap">
        {counted}

        {onCount && (filtered || totalIsEstimate) && (
          <Tooltip label={filtered ? "Count the rows this filter leaves" : "Count the rows exactly"}>
            <ActionIcon size="xs" variant="subtle" aria-label="Count rows"
              loading={counting} onClick={onCount}>
              <IconSum size={14} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      <Group gap={6} wrap="nowrap">
        <Pagination size="xs" withEdges siblings={1} boundaries={1}
          total={reachable} value={page} onChange={onPage} />

        {/* A label rather than a tooltip on these two: a tooltip that repeats the field's own name
            gives a screen reader the same words twice and a test two elements to choose between. */}
        {reachable > 5 && (
          <NumberInput size="xs" w={72} min={1} max={reachable} hideControls
            aria-label="Go to page" placeholder="page" value={jump}
            onChange={setJump}
            onBlur={goTo}
            onKeyDown={event => { if (event.key === "Enter") goTo(); }} />
        )}

        <Select size="xs" w={86} data={SIZES} value={String(pageSize)} aria-label="Rows per page"
          allowDeselect={false} comboboxProps={{ withinPortal: true }}
          onChange={value => value && onPageSize(Number(value))} />
      </Group>
    </Group>
  );
}
