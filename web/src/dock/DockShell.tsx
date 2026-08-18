import { createContext, useContext, useState } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewReadyEvent } from "dockview-react";
import { Text } from "@mantine/core";
import "dockview-react/dist/styles/dockview.css";
import "../editor/dockview-mantine.css";
import { useAppTheme } from "../ThemeProvider";
import { ExplorerTree, type ExplorerSelection } from "../explorer/ExplorerTree";
import { ObjectDetailPanel } from "../explorer/ObjectDetailPanel";

// Panels live inside dockview but are still React children of this component, so a context is the
// simplest way to feed them the current selection — no panel-parameter round trips to keep in sync.
const SelectionContext = createContext<ExplorerSelection | null>(null);

function StructurePanel() {
  return <ObjectDetailPanel selection={useContext(SelectionContext)} />;
}

function WelcomePanel() {
  return (
    <Text size="sm" c="dimmed" p="md">
      Pick a table in the explorer to see its structure. Query tabs arrive in the next phase.
    </Text>
  );
}

const components = { structure: StructurePanel, welcome: WelcomePanel };

export function DockShell() {
  const { current } = useAppTheme();
  const [selection, setSelection] = useState<ExplorerSelection | null>(null);

  const onReady = (event: DockviewReadyEvent) => {
    const welcome = event.api.addPanel({ id: "welcome", component: "welcome", title: "Start" });
    event.api.addPanel({
      id: "structure",
      component: "structure",
      title: "Structure",
      position: { referencePanel: welcome.id, direction: "right" },
    });
  };

  return (
    <SelectionContext.Provider value={selection}>
      <div style={{ display: "flex", height: "100%" }}>
        <div style={{
          width: 280, flexShrink: 0,
          borderRight: "1px solid var(--mantine-color-default-border)",
        }}>
          <ExplorerTree onSelect={setSelection} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <DockviewReact className={current.dockview} components={components} onReady={onReady} />
        </div>
      </div>
    </SelectionContext.Provider>
  );
}
