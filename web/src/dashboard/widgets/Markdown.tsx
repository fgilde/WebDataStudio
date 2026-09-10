import type { ReactNode } from "react";
import { Anchor, Code, List, Text, Title } from "@mantine/core";

/// A small Markdown subset, rendered as elements.
///
/// Elements rather than HTML on purpose: a text widget's content comes from a dashboard file, a
/// pasted Grafana JSON or somebody else's export, and none of those gets to put a script tag on
/// somebody's screen. So there is no `dangerouslySetInnerHTML` here and nothing to sanitise —
/// what is not in this subset is shown as the characters it is.
const inline = (text: string, key: string): ReactNode[] => {
  const parts: ReactNode[] = [];
  // Bold, italic, code and links, in one pass so nesting cannot produce a broken tag.
  const pattern = /\*\*(.+?)\*\*|_(.+?)_|`(.+?)`|\[(.+?)\]\((https?:\/\/[^\s)]+)\)/g;
  let last = 0;
  let match: RegExpExecArray | null;
  let index = 0;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > last) parts.push(text.slice(last, match.index));

    const id = `${key}-${index++}`;

    if (match[1]) parts.push(<strong key={id}>{match[1]}</strong>);
    else if (match[2]) parts.push(<em key={id}>{match[2]}</em>);
    else if (match[3]) parts.push(<Code key={id}>{match[3]}</Code>);
    else if (match[4]) parts.push(
      <Anchor key={id} href={match[5]} target="_blank" rel="noreferrer">{match[4]}</Anchor>,
    );

    last = pattern.lastIndex;
  }

  if (last < text.length) parts.push(text.slice(last));
  return parts;
};

export function Markdown({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  const lines = text.split(/\r?\n/);
  let bullets: string[] = [];

  const flush = (key: string) => {
    if (bullets.length === 0) return;
    blocks.push(
      <List key={key} size="sm" spacing={2}>
        {bullets.map((one, index) => (
          <List.Item key={index}>{inline(one, `${key}-${index}`)}</List.Item>
        ))}
      </List>,
    );
    bullets = [];
  };

  lines.forEach((line, index) => {
    const key = `b${index}`;
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);

    if (bullet) {
      bullets.push(bullet[1]);
      return;
    }

    flush(`l${index}`);

    if (heading) {
      blocks.push(
        <Title key={key} order={Math.min(6, heading[1].length + 2) as 3 | 4 | 5 | 6} size="h5">
          {inline(heading[2], key)}
        </Title>,
      );
      return;
    }

    if (line.trim().length === 0) return;

    blocks.push(<Text key={key} size="sm">{inline(line, key)}</Text>);
  });

  flush("l-last");

  return <>{blocks}</>;
}
