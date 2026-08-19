import { ActionIcon, Group, Tooltip } from "@mantine/core";
import { IconBrandGithub } from "@tabler/icons-react";

export const GITHUB_URL = "https://github.com/fgilde/WebDataStudio";
export const DOCS_URL = "https://fgilde.github.io/WebDataStudio/";
export const GILDE_URL = "https://www.gilde.org";

/// The three places this studio comes from, as icons. Used in the header; the login screen shows
/// the same set with labels.
export function BrandLinks({ size = 18 }: { size?: number }) {
  return (
    <Group gap={2} wrap="nowrap">
      <Tooltip label="Documentation">
        <ActionIcon component="a" href={DOCS_URL} target="_blank" rel="noreferrer"
          variant="subtle" aria-label="Documentation">
          <img src="/brand/icon.svg" alt="" width={size} height={size} style={{ display: "block" }} />
        </ActionIcon>
      </Tooltip>

      <Tooltip label="Source on GitHub">
        <ActionIcon component="a" href={GITHUB_URL} target="_blank" rel="noreferrer"
          variant="subtle" aria-label="Source on GitHub">
          <IconBrandGithub size={size} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}
