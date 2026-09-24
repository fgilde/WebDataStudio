import { useState } from "react";
import { ActionIcon, Group, ScrollArea, Stack, Text, TextInput, Tooltip, UnstyledButton } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconCopy, IconSearch } from "@tabler/icons-react";
import type { PlanPropertyDto } from "../api";
import { filterProperties } from "./planModel";

/// Everything the engine said about one operator, as SSMS's property window shows it: groups
/// that fold, a search that keeps the path to what it found, and every value copyable.
export function PlanProperties({ title, properties }: { title: string; properties: PlanPropertyDto[] }) {
  const [query, setQuery] = useState("");
  const shown = filterProperties(properties, query);

  return (
    <Stack gap={4} h="100%" style={{ minHeight: 0 }}>
      <Text size="xs" fw={700} px={6} pt={4} truncate>{title}</Text>
      <TextInput size="xs" mx={6} placeholder="Search properties" leftSection={<IconSearch size={12} />}
        value={query} onChange={e => setQuery(e.currentTarget.value)} />
      <ScrollArea style={{ flex: 1 }}>
        {shown.length === 0 && <Text size="xs" c="dimmed" p={6}>Nothing here.</Text>}
        {shown.map((p, i) => <PropertyRow key={`${p.name}-${i}`} property={p} depth={0} open={query !== ""} />)}
      </ScrollArea>
    </Stack>
  );
}

function PropertyRow({ property, depth, open }: { property: PlanPropertyDto; depth: number; open: boolean }) {
  const [expanded, setExpanded] = useState(open);
  const isOpen = expanded || open;
  const hasChildren = property.children.length > 0;

  return (
    <>
      <Group gap={4} wrap="nowrap" px={6} py={1} pl={6 + depth * 12}
        style={{ borderBottom: "1px solid var(--mantine-color-default-border)" }}>
        <UnstyledButton aria-label={hasChildren ? `Expand ${property.name}` : undefined}
          onClick={() => hasChildren && setExpanded(e => !e)}
          style={{ width: 12, visibility: hasChildren ? "visible" : "hidden" }}>
          {isOpen ? <IconChevronDown size={11} /> : <IconChevronRight size={11} />}
        </UnstyledButton>
        <Text size="xs" c="dimmed" w="45%" truncate title={property.name}>{property.name}</Text>
        <Text size="xs" ff="monospace" style={{ flex: 1, wordBreak: "break-all" }}>{property.value ?? ""}</Text>
        {property.value && (
          <Tooltip label="Copy">
            <ActionIcon size="xs" variant="subtle" aria-label={`Copy ${property.name}`}
              onClick={() => navigator.clipboard.writeText(property.value!)}>
              <IconCopy size={11} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>
      {isOpen && property.children.map((c, i) =>
        <PropertyRow key={`${c.name}-${i}`} property={c} depth={depth + 1} open={open} />)}
    </>
  );
}
