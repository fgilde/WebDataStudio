import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, DockviewGroupPanel, IDockviewPanelProps } from "dockview-react";
import { ActionIcon, Group, Text, Tooltip } from "@mantine/core";
import { IconHistory, IconSquarePlus } from "@tabler/icons-react";
import "dockview-react/dist/styles/dockview.css";
import "../editor/dockview-mantine.css";
import { useAppTheme } from "../ThemeProvider";
import { ExplorerTree, type ExplorerAction, type ExplorerSelection } from "../explorer/ExplorerTree";
import { ObjectDetailPanel } from "../explorer/ObjectDetailPanel";
import { QueryTab } from "../query/QueryTab";
import { DataTab } from "../data/DataTab";
import { HistoryPanel } from "../query/HistoryPanel";
import { PlanPanel, HealthReportPanel } from "../plan/PlanPanel";
import { TableDesigner } from "../designer/TableDesigner";
import { describeObject, listConnections, loadTabs, saveTabs, type Connection, type ForeignKeyDto } from "../api";
import { ExportDialog, type ExportTarget } from "../export/ExportDialog";
import { CopyTableDialog, ImportDialog, type ImportTarget } from "../import/ImportDialog";
import type { DialectId } from "../sql/splitStatements";

interface TabState {
  id: string; connectionId: string; dialect: DialectId; engine: string; title: string; sql: string;
}

interface DesignerTabState {
  id: string; connectionId: string; objectRef?: string; schema: string; title: string;
}

interface DataTabState {
  id: string; connectionId: string; objectRef: string; tableName: string; foreignKeys: ForeignKeyDto[];
}

interface ShellState {
  selection: ExplorerSelection | null;
  tabs: TabState[];
  dataTabs: DataTabState[];
  designerTabs: DesignerTabState[];
  updateSql: (id: string, sql: string) => void;
  openObject: (ref: string) => void;
  exportQuery: (connectionId: string, sql: string) => void;
  followForeignKey: (from: DataTabState, fk: ForeignKeyDto, value: unknown) => void;
  runStatement: (connectionId: string, sql: string) => void;
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
      engine={tab.engine}
      initialSql={seed.current}
      onSqlChange={shell.updateSql}
      onOpenObject={shell.openObject}
      onExport={sql => shell.exportQuery(tab.connectionId, sql)} />
  );
}

function DataPanel(props: IDockviewPanelProps<{ tabId: string }>) {
  const shell = useShell();
  const tab = shell.dataTabs.find(t => t.id === props.params.tabId);
  if (!tab) return <Text size="xs" c="dimmed" p="xs">This tab is gone.</Text>;

  return (
    <DataTab
      connectionId={tab.connectionId}
      objectRef={tab.objectRef}
      tableName={tab.tableName}
      foreignKeys={tab.foreignKeys}
      onFollowForeignKey={(fk, value) => shell.followForeignKey(tab, fk, value)} />
  );
}

function DesignerPanel(props: IDockviewPanelProps<{ tabId: string }>) {
  const shell = useShell();
  const tab = shell.designerTabs.find(t => t.id === props.params.tabId);
  if (!tab) return <Text size="xs" c="dimmed" p="xs">This tab is gone.</Text>;

  return <TableDesigner connectionId={tab.connectionId} objectRef={tab.objectRef} schema={tab.schema} />;
}

function PlanDockPanel() {
  const shell = useShell();
  // The plan follows whichever query tab is active; without one there is nothing to explain.
  const tab = shell.tabs[shell.tabs.length - 1];
  if (!tab) return <Text size="xs" c="dimmed" p="xs">Open a query tab to explain a statement.</Text>;

  return <PlanPanel connectionId={tab.connectionId} sql={tab.sql}
    onRunStatement={statement => shell.runStatement(tab.connectionId, statement)} />;
}

function HealthDockPanel() {
  const shell = useShell();
  const connectionId = shell.selection?.connectionId ?? shell.tabs[0]?.connectionId;
  if (!connectionId) return <Text size="xs" c="dimmed" p="xs">Select a connection first.</Text>;

  return <HealthReportPanel connectionId={connectionId} />;
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
  data: DataPanel, plan: PlanDockPanel, health: HealthDockPanel, designer: DesignerPanel,
};

export function DockShell() {
  const { current } = useAppTheme();
  const [selection, setSelection] = useState<ExplorerSelection | null>(null);
  const [connections, setConnections] = useState<Connection[]>([]);
  const [tabs, setTabs] = useState<TabState[]>([]);
  const [dataTabs, setDataTabs] = useState<DataTabState[]>([]);
  const [designerTabs, setDesignerTabs] = useState<DesignerTabState[]>([]);
  const api = useRef<DockviewApi | null>(null);
  const centerGroup = useRef<DockviewGroupPanel | null>(null);
  const restored = useRef(false);
  const [exportTarget, setExportTarget] = useState<ExportTarget | null>(null);
  const [importTarget, setImportTarget] = useState<ImportTarget | null>(null);
  const [copySource, setCopySource] = useState<{ connectionId: string; objectRef: string; label: string } | null>(null);

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
      engine: connection.engine,
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
      id: "plan", component: "plan", title: "Plan",
      position: { referencePanel: structure.id, direction: "within" },
    });
    event.api.addPanel({
      id: "health", component: "health", title: "Health",
      position: { referencePanel: structure.id, direction: "within" },
    });
    event.api.addPanel({
      id: "history", component: "history", title: "History",
      position: { referencePanel: structure.id, direction: "below" },
    });
    event.api.getPanel("structure")?.api.setActive();

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

  const openDesigner = useCallback((connectionId: string, objectRef: string | undefined,
    schema: string, title: string) => {
    const tab: DesignerTabState = {
      id: "s" + Date.now().toString(36), connectionId, objectRef, schema, title,
    };
    setDesignerTabs(list => [...list, tab]);
    api.current?.addPanel({
      id: tab.id, component: "designer", title, params: { tabId: tab.id },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
  }, []);

  const openData = useCallback(async (connectionId: string, objectRef: string, tableName: string) => {
    const detail = await describeObject(connectionId, objectRef).catch(() => null);
    const tab: DataTabState = {
      id: "d" + Date.now().toString(36),
      connectionId, objectRef, tableName,
      foreignKeys: detail?.foreignKeys ?? [],
    };
    setDataTabs(list => [...list, tab]);
    api.current?.addPanel({
      id: tab.id, component: "data", title: tableName, params: { tabId: tab.id },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
  }, []);

  // Following a foreign key lands on the referenced row itself, not on the whole table.
  const followForeignKey = useCallback((from: DataTabState, fk: ForeignKeyDto, value: unknown) => {
    const literal = typeof value === "number" ? String(value) : "'" + String(value).replace(/'/g, "''") + "'";
    newTab(from.connectionId,
      "SELECT * FROM " + fk.referencedTable + " WHERE " + fk.referencedColumns[0] + " = " + literal);
  }, [newTab]);

  // Qualifies an object the way its engine expects, so a generated SELECT runs as-is.
  const qualify = useCallback((connectionId: string, ref: string) => {
    const parsed = ref.split(":", 2)[1]?.split("/") ?? [];
    const connection = connections.find(c => c.id === connectionId);
    const multiSchema = connection && !["sqlite", "duckdb"].includes(connection.engine);
    return multiSchema && parsed.length > 1 ? `${parsed[0]}.${parsed[parsed.length - 1]}` : parsed[parsed.length - 1];
  }, [connections]);

  const handleAction = useCallback(async (action: ExplorerAction, s: ExplorerSelection) => {
    const name = qualify(s.connectionId, s.node.ref);

    switch (action) {
      case "design":
        openDesigner(s.connectionId, s.node.ref,
          s.node.ref.split(":")[1]?.split("/")[0] ?? "", `design ${s.node.label}`);
        break;

      case "new-table":
        openDesigner(s.connectionId, undefined,
          s.node.ref.split(":")[1]?.split("/")[0] ?? "", "new table");
        break;

      case "open-data":
        await openData(s.connectionId, s.node.ref, s.node.label);
        break;

      case "new-query":
        newTab(s.connectionId, `SELECT * FROM ${name}`);
        break;

      case "copy-name":
        await navigator.clipboard.writeText(name);
        break;

      case "show-ddl": {
        const detail = await describeObject(s.connectionId, s.node.ref).catch(() => null);
        // Engines that do not hand out DDL still get something useful: the column list as a comment.
        const columnList = detail?.columns.map(c => `-- ${c.name} ${c.dataType}`).join("\n")
          ?? "-- no detail available";
        const ddl = detail?.ddl ?? [`-- ${name}`, columnList].join("\n");
        newTab(s.connectionId, ddl);
        break;
      }

      case "import":
        setImportTarget({ connectionId: s.connectionId, table: name });
        break;

      case "copy-table":
        setCopySource({ connectionId: s.connectionId, objectRef: s.node.ref, label: s.node.label });
        break;

      case "export":
        setExportTarget({
          connectionId: s.connectionId,
          objectRef: s.node.ref,
          schema: s.node.ref.split(":", 2)[1]?.split("/")[0],
          defaultName: s.node.label,
          scopes: ["table", "schema"],
        });
        break;

      default:
        setSelection(s);
    }
  }, [newTab, openData, openDesigner, qualify]);

  const runStatement = useCallback((connectionId: string, sql: string) => newTab(connectionId, sql), [newTab]);

  const exportQuery = useCallback((connectionId: string, sql: string) =>
    setExportTarget({ connectionId, sql, defaultName: "result", scopes: ["result"] }), []);

  const activeConnection = selection?.connectionId ?? connections[0]?.id ?? "";

  return (
    <ShellContext.Provider value={{ selection, tabs, dataTabs, designerTabs, updateSql, openObject, exportQuery, followForeignKey, runStatement }}>
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
            <ExplorerTree onSelect={setSelection} onAction={handleAction} />
          </div>
        </div>

        <div style={{ flex: 1, minWidth: 0 }}>
          <DockviewReact className={current.dockview} components={components} onReady={onReady} />
        </div>
      </div>

      <ExportDialog target={exportTarget} onClose={() => setExportTarget(null)} />
      <ImportDialog target={importTarget} onClose={() => setImportTarget(null)} />
      <CopyTableDialog source={copySource} connections={connections}
        onClose={() => setCopySource(null)} />
    </ShellContext.Provider>
  );
}
