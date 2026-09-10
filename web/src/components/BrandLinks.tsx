import { ActionIcon, Group, Tooltip } from "@mantine/core";
import { IconBrandGithub } from "@tabler/icons-react";

export const GITHUB_URL = "https://github.com/fgilde/WebDataStudio";
// Straight into the guide: the landing page is for people who do not have the studio in front of
// them yet.
export const DOCS_URL = "https://fgilde.github.io/WebDataStudio/guide/";
export const GILDE_URL = "https://www.gilde.org";
/// The product's own page, for somebody who wants the pitch rather than the repository.
export const SITE_URL = "https://fgilde.github.io/WebDataStudio";

/// The two places this studio comes from, as icons. Used on the shared-result page, which belongs
/// to whoever has the link and has no header to open an about drawer from.
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
