import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, DockviewGroupPanel, IDockviewPanelProps } from "dockview-react";
import { ActionIcon, Group, Text, Tooltip } from "@mantine/core";
import {
  IconBookmarks, IconCommand, IconGitCompare, IconHistory, IconLayoutBoard, IconSettingsCog,
  IconSitemap, IconSquarePlus, IconTable,
} from "@tabler/icons-react";
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
import { DiagramPanel } from "../diagram/DiagramPanel";
import { AdminPanel } from "../admin/AdminPanel";
import { ComparePanel } from "../compare/ComparePanel";
import { QueryDesigner } from "../designer/QueryDesigner";
import { SavedQueriesPanel } from "../query/SavedQueriesPanel";
import { SnippetManager } from "../editor/SnippetManager";
import { CommandPalette, ShortcutsHelp } from "../shell/CommandPalette";
import { LayoutPresetsModal } from "../shell/LayoutPresets";
import { buildCommands } from "../shell/commands";
import { buildDeepLink, parseDeepLink } from "../shell/deepLink";
import { GoToObject } from "../shell/GoToObject";
import {
  applyDdl, describeObject, listConnections, loadTabs, previewRename, saveTabs,
  type Connection, type ForeignKeyDto,
} from "../api";
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
  openData: (connectionId: string, objectRef: string, tableName: string) => void;
  dialectOf: (connectionId: string) => DialectId;
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
  const shell = useShell();
  const connectionId = shell.selection?.connectionId ?? shell.tabs[0]?.connectionId ?? "";

  return <HistoryPanel onOpen={entry =>
    shell.runStatement(entry.connectionId || connectionId, entry.sql)} />;
}

function SavedQueriesDockPanel() {
  const shell = useShell();
  const current = shell.tabs[shell.tabs.length - 1];

  return (
    <SavedQueriesPanel currentSql={current?.sql} currentConnectionId={current?.connectionId}
      onOpen={query => shell.runStatement(
        query.connectionId ?? current?.connectionId ?? "", query.sql)} />
  );
}

function QueryDesignerDockPanel(props: IDockviewPanelProps<{ connectionId: string }>) {
  const shell = useShell();
  const connection = props.params.connectionId;

  return (
    <QueryDesigner connectionId={connection} dialect={shell.dialectOf(connection)}
      onOpenInTab={sql => shell.runStatement(connection, sql)} />
  );
}

function DiagramDockPanel(props: IDockviewPanelProps<{ connectionId: string }>) {
  const shell = useShell();
  return <DiagramPanel connectionId={props.params.connectionId} onOpenTable={shell.openData} />;
}

function AdminDockPanel(props: IDockviewPanelProps<{ connectionId: string }>) {
  return <AdminPanel connectionId={props.params.connectionId} />;
}

function CompareDockPanel(props: IDockviewPanelProps<{ connectionId: string }>) {
  return <ComparePanel connectionId={props.params.connectionId} />;
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
  diagram: DiagramDockPanel, admin: AdminDockPanel, compare: CompareDockPanel,
  saved: SavedQueriesDockPanel, builder: QueryDesignerDockPanel,
};

/// The default arrangement, in one place: the initial layout and the reset command must produce
/// the same thing, or reset becomes its own surprise.
function buildDefaultLayout(
  api: DockviewApi, centerGroup: React.MutableRefObject<DockviewGroupPanel | null>) {
  const welcome = api.addPanel({ id: "welcome", component: "welcome", title: "Start" });
  centerGroup.current = welcome.group;

  const structure = api.addPanel({
    id: "structure", component: "structure", title: "Structure",
    position: { referencePanel: welcome.id, direction: "right" },
    initialWidth: 340,
  });
  api.addPanel({
    id: "plan", component: "plan", title: "Plan",
    position: { referencePanel: structure.id, direction: "within" },
  });
  api.addPanel({
    id: "health", component: "health", title: "Health",
    position: { referencePanel: structure.id, direction: "within" },
  });
  api.addPanel({
    id: "history", component: "history", title: "History",
    position: { referencePanel: structure.id, direction: "below" },
  });
  api.addPanel({
    id: "saved", component: "saved", title: "Saved",
    position: { referencePanel: "history", direction: "within" },
  });

  api.getPanel("structure")?.api.setActive();
  welcome.api.setActive();
}

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
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [snippetsOpen, setSnippetsOpen] = useState(false);
  const [layoutsOpen, setLayoutsOpen] = useState(false);
  const [explorerNonce, setExplorerNonce] = useState(0);
  const [gotoOpen, setGotoOpen] = useState(false);

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
    buildDefaultLayout(event.api, centerGroup);
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

      case "rename": {
        const next = window.prompt(`Rename ${s.node.label} to`, s.node.label);
        if (!next || next === s.node.label) break;

        const preview = await previewRename(s.connectionId, s.node.ref, next).catch(e => {
          window.alert(e.message);
          return null;
        });

        // The dependency list is the point of the prompt: a rename can break a view.
        if (preview && window.confirm(
          `${preview.script}

Used by: ${preview.dependencies.usedBy.join(", ") || "nothing found"}`))
          await applyDdl(s.connectionId, preview.hash).catch(e => window.alert(e.message));
        break;
      }

      case "script-insert":
      case "script-update":
      case "script-delete":
      case "script-truncate":
      case "script-drop": {
        const detail = await describeObject(s.connectionId, s.node.ref).catch(() => null);
        const columns = detail?.columns.map(c => c.name) ?? [];
        const keys = detail?.columns.filter(c => c.isPrimaryKey).map(c => c.name) ?? [];
        const where = (keys.length > 0 ? keys : columns.slice(0, 1))
          .map(c => `${c} = ?`).join(" AND ") || "1 = 0";

        const assignments = columns.filter(c => !keys.includes(c))
          .map(c => `${c} = ?`).join(",\n       ");

        const script =
          action === "script-insert"
            ? `INSERT INTO ${name} (${columns.join(", ")})\nVALUES (${columns.map(() => "?").join(", ")});`
            : action === "script-update"
              ? `UPDATE ${name}\n   SET ${assignments}\n WHERE ${where};`
              : action === "script-delete"
                ? `DELETE FROM ${name}\n WHERE ${where};`
                : action === "script-truncate"
                  ? `-- review before running\nTRUNCATE TABLE ${name};`
                  : `-- review before running\nDROP TABLE ${name};`;

        newTab(s.connectionId, script);
        break;
      }

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

  const dialectOf = useCallback((connectionId: string) =>
    dialectFor(connections.find(c => c.id === connectionId)?.engine ?? "postgresql"), [connections]);

  const activeConnection = selection?.connectionId ?? connections[0]?.id ?? "";

  // One panel per tool and connection: clicking the button again focuses the panel that is
  // already open instead of stacking duplicates.
  const openTool = useCallback((component: string, title: string, connectionId: string) => {
    if (!connectionId) return;
    const id = `${component}:${connectionId}`;
    const existing = api.current?.getPanel(id);
    if (existing) { existing.api.setActive(); return; }

    api.current?.addPanel({
      id, component, title, params: { connectionId },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
  }, []);

  const resetLayout = useCallback(() => {
    // The way back from a layout with every panel closed: rebuild the default arrangement.
    api.current?.clear();
    if (api.current) buildDefaultLayout(api.current, centerGroup);
  }, []);

  const commands = useMemo(() => buildCommands({
    newQuery: () => newTab(activeConnection),
    runCurrent: () => document.dispatchEvent(new KeyboardEvent("keydown", { key: "F5" })),
    cancelCurrent: () => document.dispatchEvent(
      new KeyboardEvent("keydown", { key: "c", ctrlKey: true, shiftKey: true })),
    formatCurrent: () => document.dispatchEvent(
      new KeyboardEvent("keydown", { key: "f", ctrlKey: true, shiftKey: true })),
    openConnections: () => { window.location.hash = "#/connections"; },
    addConnection: () => { window.location.hash = "#/connections?add=1"; },
    refreshExplorer: () => setExplorerNonce(n => n + 1),
    goToObject: () => setGotoOpen(true),
    openDiagram: () => openTool("diagram", "Diagram", activeConnection),
    openHealth: () => api.current?.getPanel("health")?.api.setActive(),
    openAdmin: () => openTool("admin", "Admin", activeConnection),
    openCompare: () => openTool("compare", "Compare", activeConnection),
    openHistory: () => api.current?.getPanel("history")?.api.setActive(),
    openSavedQueries: () => api.current?.getPanel("saved")?.api.setActive(),
    saveCurrentQuery: () => api.current?.getPanel("saved")?.api.setActive(),
    exportResult: () => {
      const tab = tabs[tabs.length - 1];
      if (tab) exportQuery(tab.connectionId, tab.sql);
    },
    openSnippets: () => setSnippetsOpen(true),
    switchTheme: () => document.dispatchEvent(new CustomEvent("wds:cycle-theme")),
    saveLayout: () => setLayoutsOpen(true),
    resetLayout,
    copyLink: () => {
      if (!selection) return;
      const link = buildDeepLink({
        kind: "object", connectionId: selection.connectionId, objectRef: selection.node.ref,
      });
      void navigator.clipboard.writeText(`${window.location.origin}${window.location.pathname}${link}`);
    },
    showShortcuts: () => setShortcutsOpen(true),
  }), [activeConnection, newTab, openTool, resetLayout, selection, tabs, exportQuery]);

  // Ctrl+K everywhere, "?" only outside a text field — otherwise it eats a question mark.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target?.closest("input, textarea, .monaco-editor") !== null;

      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setPaletteOpen(true);
        return;
      }
      if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === "o") {
        event.preventDefault();
        setGotoOpen(true);
        return;
      }
      if (event.key === "?" && !typing) {
        event.preventDefault();
        setShortcutsOpen(true);
      }
    };

    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  // A deep link opens its target once the connections are known.
  const followedLink = useRef(false);
  useEffect(() => {
    if (followedLink.current || connections.length === 0) return;
    followedLink.current = true;

    const link = parseDeepLink(window.location.hash);
    if (!link || !connections.some(c => c.id === link.connectionId)) return;

    if (link.kind === "object")
      void openData(link.connectionId, link.objectRef, link.objectRef.split("/").pop() ?? "table");
  }, [connections, openData]);

  return (
    <ShellContext.Provider value={{ selection, tabs, dataTabs, designerTabs, updateSql, openObject, exportQuery, followForeignKey, runStatement, openData, dialectOf }}>
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
              <Tooltip label="Diagram">
                <ActionIcon size="sm" variant="subtle" aria-label="Diagram" disabled={!activeConnection}
                  onClick={() => openTool("diagram", "Diagram", activeConnection)}>
                  <IconSitemap size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Compare">
                <ActionIcon size="sm" variant="subtle" aria-label="Compare" disabled={!activeConnection}
                  onClick={() => openTool("compare", "Compare", activeConnection)}>
                  <IconGitCompare size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Administration">
                <ActionIcon size="sm" variant="subtle" aria-label="Administration" disabled={!activeConnection}
                  onClick={() => openTool("admin", "Admin", activeConnection)}>
                  <IconSettingsCog size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Query builder">
                <ActionIcon size="sm" variant="subtle" aria-label="Query builder" disabled={!activeConnection}
                  onClick={() => openTool("builder", "Builder", activeConnection)}>
                  <IconTable size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Saved queries">
                <ActionIcon size="sm" variant="subtle" aria-label="Saved queries"
                  onClick={() => api.current?.getPanel("saved")?.api.setActive()}>
                  <IconBookmarks size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Layout presets">
                <ActionIcon size="sm" variant="subtle" aria-label="Layout presets"
                  onClick={() => setLayoutsOpen(true)}>
                  <IconLayoutBoard size={15} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label="Command palette (Ctrl+K)">
                <ActionIcon size="sm" variant="subtle" aria-label="Command palette"
                  onClick={() => setPaletteOpen(true)}>
                  <IconCommand size={15} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Group>
          <div style={{ flex: 1, minHeight: 0 }}>
            <ExplorerTree key={explorerNonce} onSelect={setSelection} onAction={handleAction} />
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

      <GoToObject connectionId={activeConnection} opened={gotoOpen} onClose={() => setGotoOpen(false)}
        onPick={table => openData(activeConnection, table.ref, table.name)} />
      <CommandPalette commands={commands} opened={paletteOpen} onClose={() => setPaletteOpen(false)} />
      <ShortcutsHelp commands={commands} opened={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
      <SnippetManager opened={snippetsOpen} onClose={() => setSnippetsOpen(false)} />
      <LayoutPresetsModal opened={layoutsOpen} onClose={() => setLayoutsOpen(false)}
        connectionId={activeConnection || null}
        capture={() => api.current?.toJSON()}
        apply={layout => { try { api.current?.fromJSON(layout as never); } catch { resetLayout(); } }}
        reset={resetLayout} />
    </ShellContext.Provider>
  );
}
