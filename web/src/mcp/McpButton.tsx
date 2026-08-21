import { useEffect, useState } from "react";
import { ActionIcon, Indicator, Tooltip } from "@mantine/core";
import { IconPlugConnected, IconPlugConnectedX } from "@tabler/icons-react";
import { health, type HealthDto } from "../api";
import { McpModal } from "./McpModal";

type Mcp = NonNullable<HealthDto["mcp"]>;

/// The way to hand this studio to an agent. Absent unless somebody asked for an MCP endpoint — and
/// when they asked for one the studio refuses to serve, the icon says that rather than pretending
/// everything is fine.
export function McpButton() {
  const [mcp, setMcp] = useState<Mcp | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    health().then(state => setMcp(state.mcp ?? null)).catch(() => setMcp(null));
  }, []);

  if (!mcp) return null;

  const broken = mcp.enabled === false;

  return (
    <>
      <Tooltip label={broken
        ? "MCP is configured but not being served — click for the reason"
        : "Use this studio from an AI agent (MCP)"}>
        {/* The dot says "this endpoint can write", which is the one thing worth noticing. */}
        <Indicator disabled={!mcp.writes && !broken} color={broken ? "red" : "orange"}
          size={6} offset={4}>
          <ActionIcon variant="subtle" aria-label="MCP" color={broken ? "red" : undefined}
            onClick={() => setOpen(true)}>
            {broken ? <IconPlugConnectedX size={18} /> : <IconPlugConnected size={18} />}
          </ActionIcon>
        </Indicator>
      </Tooltip>

      <McpModal opened={open} onClose={() => setOpen(false)} path={mcp.path}
        needsKey={mcp.needsKey} enabled={mcp.enabled !== false} reason={mcp.reason} />
    </>
  );
}
