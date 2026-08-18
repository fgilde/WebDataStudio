import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, DockviewGroupPanel, IDockviewPanelProps } from "dockview-react";
import { ActionIcon, Group, Text, Tooltip } from "@mantine/core";
import { IconHistory, IconSquarePlus } from "@tabler/icons-react";
import "dockview-react/dist/styles/dockview.css";
import "../editor/dockview-mantine.css";
import { useAppTheme } from "../ThemeProvider";
import { ExplorerTree, type ExplorerSelection } from "../explorer/ExplorerTree";
import { ObjectDetailPanel } from "../explorer/ObjectDetailPanel";
import { QueryTab } from "../query/QueryTab";
import { HistoryPanel } from "../query/HistoryPanel";
import { listConnections, loadTabs, saveTabs, type Connection } from "../api";
import type { DialectId } from "../sql/splitStatements";

interface TabState { id: string; connectionId: string; dialect: DialectId; title: string; sql: string }

interface ShellState {
  selection: ExplorerSelection | null;
  tabs: TabState[];
  updateSql: (id: string, sql: string) => void;
  openObject: (ref: string) => void;
}

const ShellContext = createContext<ShellState | null>(null);
const useShell = () => useContext(ShellContext)!;

// Engines without their own dialect entry fall back to PostgreSQL syntax, which is the closest
// thing to standard SQL among the ones we support.
const DIALECTS: Record<string, DialectId> = {
  postgresql: "postgresql", mysql: "mysql", sqlserver: "sqlserver", sqlite: "sqlite",
  oracle: "oracle", duckdb: "duckdb", clickhouse: "clickhouse",
};
export const dialectFor = (engine: string): DialectId => DIALECTS[engine] ?? "postgresql";

function StructurePanel() {
  return <ObjectDetailPanel selection={useShell().selection} />;
}

function QueryPanel(props: IDockviewPanelProps<{ tabId: string }>) {
  const shell = useShell();
  const tab = shell.tabs.find(t => t.id === props.params.tabId);
  // The stored SQL seeds the editor once. Reading it on every render would feed the editor its own
  // output one keystroke late and fight the cursor.
  const seed = useRef(tab?.sql ?? "");

  if (!tab) return <Text size="xs" c="dimmed" p="xs">This tab is gone.</Text>;

  return (
    <QueryTab
      tabId={tab.id}
      connectionId={tab.connectionId}
      dialect={tab.dialect}
      initialSql={seed.current}
      onSqlChange={shell.updateSql}
      onOpenObject={shell.openObject} />
  );
}

function HistoryDockPanel() {
  return <HistoryPanel onOpen={() => { /* opening into a tab lands in P9's command palette work */ }} />;
}

function WelcomePanel() {
  return (
    <Text size="sm" c="dimmed" p="md">
      Pick a table in the explorer, or press the new-query button above it to start writing SQL.
    </Text>
  );
}

const components = {
  structure: StructurePanel, query: QueryPanel, history: HistoryDockPanel, welcome: WelcomePanel,
};

export function DockShell() {
  const { current } = useAppTheme();
  const [selection, setSelection] = useState<ExplorerSelection | null>(null);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [tabs, setTabs] = useState<TabState[]>([]);
  const api = useRef<DockviewApi | null>(null);
  const centerGroup = useRef<DockviewGroupPanel | null>(null);
  const restored = useRef(false);

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, []);

  const updateSql = useCallback((id: string, sql: string) => {
    setTabs(list => list.map(t => (t.id === id ? { ...t, sql } : t)));
  }, []);

  const openObject = useCallback((ref: string) => {
    // Landing on the object in the Structure panel is enough for now; the explorer keeps its own
    // expansion state, and forcing it open from here would fight the user's navigation.
    setSelection(s => (s ? { ...s, node: { ...s.node, ref } } : s));
  }, []);

  // Query tabs always join the centre group; Structure and History keep their own column on the
  // right, otherwise the first panel created wins the middle and the editor ends up in a corner.
  const addPanelFor = useCallback((tab: TabState) => {
    api.current?.addPanel({
      id: tab.id, component: "query", title: tab.title, params: { tabId: tab.id },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
  }, []);

  const newTab = useCallback((connectionId: string, sql = "") => {
    const connection = connections.find(c => c.id === connectionId) ?? connections[0];
    if (!connection) return;

    const tab: TabState = {
      id: `q${Date.now().toString(36)}`,
      connectionId: connection.id,
      dialect: dialectFor(connection.engine),
      title: `${connection.name} · query`,
      sql,
    };
    setTabs(list => [...list, tab]);
    addPanelFor(tab);
  }, [connections, addPanelFor]);

  const onReady = (event: DockviewReadyEvent) => {
    api.current = event.api;

    const welcome = event.api.addPanel({ id: "welcome", component: "welcome", title: "Start" });
    centerGroup.current = welcome.group;

    const structure = event.api.addPanel({
      id: "structure", component: "structure", title: "Structure",
      position: { referencePanel: welcome.id, direction: "right" },
      initialWidth: 340,
    });
    event.api.addPanel({
      id: "history", component: "history", title: "History",
      position: { referencePanel: structure.id, direction: "below" },
    });

    welcome.api.setActive();
  };

  // Restore the tabs the server remembers, once dockview and the connection list are both ready.
  useEffect(() => {
    if (restored.current || !api.current || connections.length === 0) return;
    restored.current = true;

    loadTabs()
      .then(stored => {
        const valid = (stored as TabState[]).filter(t => connections.some(c => c.id === t.connectionId));
        setTabs(valid);
        valid.forEach(addPanelFor);
      })
      .catch(() => { /* a fresh workspace simply has no tabs */ });
  }, [connections, addPanelFor]);

  // Persist tabs, debounced: every keystroke would otherwise be a round trip.
  useEffect(() => {
    if (!restored.current) return;
    const timer = setTimeout(() => { saveTabs(tabs).catch(() => {}); }, 500);
    return () => clearTimeout(timer);
  }, [tabs]);

  const activeConnection = selection?.connectionId ?? connections[0]?.id ?? "";

  return (
    <ShellContext.Provider value={{ selection, tabs, updateSql, openObject }}>
      <div style={{ display: "flex", height: "100%" }}>
        <div style={{
          width: 280, flexShrink: 0, display: "flex", flexDirection: "column",
          borderRight: "1px solid var(--mantine-color-default-border)",
        }}>
          <Group gap={4} px={4} pt={4} justify="space-between">
            <Text size="xs" fw={600} c="dimmed">EXPLORER</Text>
            <Group gap={2}>
              <Tooltip label="New query">
                <ActionIcon size="sm" variant="subtle" aria-label="New query" disabled={!activeConnection}
                  onClick={() => newTab(activeConnection)}>
                  <IconSquarePlus size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="History">
                <ActionIcon size="sm" variant="subtle" aria-label="History"
                  onClick={() => api.current?.getPanel("history")?.api.setActive()}>
                  <IconHistory size={15} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Group>
          <div style={{ flex: 1, minHeight: 0 }}>
            <ExplorerTree onSelect={setSelection} />
          </div>
        </div>

        <div style={{ flex: 1, minWidth: 0 }}>
          <DockviewReact className={current.dockview} components={components} onReady={onReady} />
        </div>
      </div>
    </ShellContext.Provider>
  );
}
