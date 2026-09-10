import { useEffect, useState } from "react";
import {
  Anchor, Badge, Button, Code, Divider, Drawer, Group, Stack, Text,
} from "@mantine/core";
import { IconBrandGithub, IconBook2, IconExternalLink, IconWorld } from "@tabler/icons-react";
import { health, type HealthDto } from "../api";
import { DOCS_URL, GILDE_URL, GITHUB_URL, SITE_URL } from "./BrandLinks";
import "./AboutDrawer.css";

/// gilde.org's own mark, from https://www.gilde.org/gilde/logo.svg. Inline rather than an `img`
/// because the animation is the drawing of its stroke, and CSS cannot reach into an image.
function GildeMark({ size = 26 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 512 512" aria-hidden className="wds-about-draw">
      <defs>
        <linearGradient id="wds-gilde-gold" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#ffe6a8" />
          <stop offset="0.45" stopColor="#e0a24e" />
          <stop offset="1" stopColor="#a9761f" />
        </linearGradient>
      </defs>
      <g fill="none" stroke="url(#wds-gilde-gold)" strokeWidth="56" strokeLinecap="square"
        strokeLinejoin="miter">
        <path d="M376 88H112v264l144 112 144-112v-96H280" pathLength={1} />
        <path d="M280 216h120" pathLength={1} />
      </g>
    </svg>
  );
}

const when = (value: string | undefined) => {
  const date = value ? new Date(value) : null;
  return date && !Number.isNaN(date.getTime()) ? date.toLocaleString() : null;
};

/// What this studio is, which build of it is running, and where it comes from.
///
/// The two link buttons that used to sit in the header live here now: the header is for what people
/// do all day, and "where does this come from" is a question somebody asks once.
export function AboutDrawer({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const [info, setInfo] = useState<HealthDto | null>(null);

  // Only once it is opened: nobody needs a round trip for a drawer they never touch.
  useEffect(() => {
    if (!opened || info) return;
    health().then(setInfo).catch(() => setInfo(null));
  }, [opened, info]);

  const [version, commit] = (info?.version ?? "").split("+");
  const built = when(info?.built);

  return (
    <Drawer opened={opened} onClose={onClose} position="right" size={380} padding="md"
      title="About">
      <Stack gap="lg">
        <Stack align="center" gap="xs" pt="xs">
          <img src="/brand/logo.svg" alt="WebDataStudio" height={40} className="wds-about-logo"
            style={{ display: "block" }} />
          <Text size="xs" c="dimmed" ta="center">
            Every database this team has, in one browser tab.
          </Text>
        </Stack>

        <Stack gap={6}>
          <Group justify="space-between" wrap="nowrap">
            <Text size="sm" c="dimmed">Version</Text>
            {version
              ? <Badge variant="light" size="sm" tt="none">v{version}</Badge>
              : <Text size="sm" c="dimmed">…</Text>}
          </Group>

          <Group justify="space-between" wrap="nowrap">
            <Text size="sm" c="dimmed">Build</Text>
            <Code>{commit && commit !== "local" ? commit.slice(0, 7) : "local"}</Code>
          </Group>

          {built ? (
            <Group justify="space-between" wrap="nowrap">
              <Text size="sm" c="dimmed">Built</Text>
              <Text size="sm">{built}</Text>
            </Group>
          ) : null}
        </Stack>

        <Divider />

        <Stack gap="xs">
          <Button component="a" href={DOCS_URL} target="_blank" rel="noreferrer"
            variant="light" size="md" justify="space-between" fullWidth
            leftSection={<IconBook2 size={18} />} rightSection={<IconExternalLink size={14} />}>
            Documentation
          </Button>

          <Button component="a" href={GITHUB_URL} target="_blank" rel="noreferrer"
            variant="light" size="md" justify="space-between" fullWidth color="gray"
            leftSection={<IconBrandGithub size={18} />}
            rightSection={<IconExternalLink size={14} />}>
            Source on GitHub
          </Button>

          <Button component="a" href={SITE_URL} target="_blank" rel="noreferrer"
            variant="light" size="md" justify="space-between" fullWidth color="teal"
            leftSection={<IconWorld size={18} />} rightSection={<IconExternalLink size={14} />}>
            Website
          </Button>
        </Stack>

        <Divider label="made at" labelPosition="center" />

        <Anchor href={GILDE_URL} target="_blank" rel="noreferrer" underline="never" c="dimmed"
          ta="center">
          <Group gap={8} justify="center" wrap="nowrap">
            <GildeMark />
            <Text size="sm" fw={600}>gilde.org</Text>
          </Group>
        </Anchor>
      </Stack>
    </Drawer>
  );
}
