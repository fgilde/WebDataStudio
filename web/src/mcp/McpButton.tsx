import { useEffect, useState } from "react";
import { ActionIcon, Indicator, Tooltip } from "@mantine/core";
import { IconPlugConnected } from "@tabler/icons-react";
import { health } from "../api";
import { McpModal } from "./McpModal";

/// The way to hand this studio to an agent. Absent unless the studio really is an MCP server: a
/// button that opens instructions for a feature nobody switched on is just noise.
export function McpButton() {
  const [mcp, setMcp] = useState<{ path: string; writes: boolean; needsKey: boolean } | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    health().then(state => setMcp(state.mcp ?? null)).catch(() => setMcp(null));
  }, []);

  if (!mcp) return null;

  return (
    <>
      <Tooltip label="Use this studio from an AI agent (MCP)">
        {/* The dot says "this endpoint can write", which is the one thing worth noticing. */}
        <Indicator disabled={!mcp.writes} color="orange" size={6} offset={4}>
          <ActionIcon variant="subtle" aria-label="MCP" onClick={() => setOpen(true)}>
            <IconPlugConnected size={18} />
          </ActionIcon>
        </Indicator>
      </Tooltip>

      <McpModal opened={open} onClose={() => setOpen(false)}
        path={mcp.path} needsKey={mcp.needsKey} />
    </>
  );
}
