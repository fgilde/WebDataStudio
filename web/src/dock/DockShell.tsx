import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { DockviewReact } from "dockview-react";
import type { DockviewApi, DockviewReadyEvent, DockviewGroupPanel, IDockviewPanelProps } from "dockview-react";
import { ActionIcon, Group, Modal, Text, Tooltip } from "@mantine/core";
import {
  IconArrowsJoin, IconNotebook,
  IconBookmarks, IconCommand, IconGitCompare, IconHistory, IconKey, IconLayoutBoard,
  IconSettingsCog, IconSitemap, IconSquarePlus, IconTable,
} from "@tabler/icons-react";
import "dockview-react/dist/styles/dockview.css";
import "../editor/dockview-mantine.css";
import { useAppTheme } from "../ThemeProvider";
import { FederationPanel } from "../federate/FederationPanel";
import { NotebookPanel } from "../notebook/NotebookPanel";
import { isAdmin, useRole } from "../auth/useRole";
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
import { RedisPanel } from "../redis/RedisPanel";
import { parseModel } from "../designer/buildSelect";
import { SavedQueriesPanel } from "../query/SavedQueriesPanel";
import { SnippetManager } from "../editor/SnippetManager";
import { CommandPalette, ShortcutsHelp } from "../shell/CommandPalette";
import { StudioTab, TabPinsProvider, type TabPins } from "./StudioTab";
import { VersionBadge } from "../shell/VersionBadge";
import { LayoutPresetsModal, presetForSlot, useLayoutPresets } from "../shell/LayoutPresets";
import { buildCommands } from "../shell/commands";
import { buildDeepLink, parseDeepLink } from "../shell/deepLink";
import { GoToObject } from "../shell/GoToObject";
import { NewDatabaseDialog, DropDatabaseDialog, type DatabaseTarget } from "../explorer/DatabaseDialogs";
import { PropertiesDialog } from "../explorer/PropertiesDialog";
import {
  dropColumn, dropConstraint, dropIndex, executeRoutine, rebuildIndex, refreshMaterializedView,
  selectColumn,
} from "../sql/objectScripts";
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
  connections: Connection[];
  selection: ExplorerSelection | null;
  tabs: TabState[];
  dataTabs: DataTabState[];
  designerTabs: DesignerTabState[];
  updateSql: (id: string, sql: string) => void;
  openObject: (ref: string) => void;
  exportQuery: (connectionId: string, sql: string) => void;
  exportObject: (connectionId: string, objectRef: string, label: string) => void;
  followForeignKey: (from: DataTabState, fk: ForeignKeyDto, value: unknown) => void;
  runStatement: (connectionId: string, sql: string) => void;
  openData: (connectionId: string, objectRef: string, tableName: string) => void;
  dialectOf: (connectionId: string) => DialectId;
  // The explorer is a dock panel like any other, so it can be dragged, split off or closed. What
  // it needs from the shell travels through here instead of through props.
  explorer: {
    nonce: number;
    activeConnection: string;
    select: (selection: ExplorerSelection) => void;
    action: (action: ExplorerAction, selection: ExplorerSelection) => void;
    newQuery: () => void;
    focusPanel: (id: string) => void;
    openTool: (component: string, title: string, connectionId: string) => void;
    openLayouts: () => void;
    openPalette: () => void;
    engineOf: (connectionId: string) => string;
  };
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
  const shell = useShell();

  return (
    <ObjectDetailPanel selection={shell.selection}
      // The SQL tab and the privilege statements open a query tab rather than running anything:
      // a GRANT goes through the editor's preview like every other change.
      onOpenInEditor={sql => shell.selection
        && shell.runStatement(shell.selection.connectionId, sql)} />
  );
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
      onFollowForeignKey={(fk, value) => shell.followForeignKey(tab, fk, value)}
      onExport={() => shell.exportObject(tab.connectionId, tab.objectRef, tab.tableName)} />
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

function QueryDesignerDockPanel(
  props: IDockviewPanelProps<{ connectionId: string; sql?: string }>) {
  const shell = useShell();
  const connection = props.params.connectionId;

  // A statement the builder produced carries its model in a comment; anything else opens empty.
  const initialModel = props.params.sql ? parseModel(props.params.sql) ?? undefined : undefined;

  return (
    <QueryDesigner connectionId={connection} dialect={shell.dialectOf(connection)}
      initialModel={initialModel}
      onOpenInTab={sql => shell.runStatement(connection, sql)} />
  );
}

function RedisDockPanel(
  props: IDockviewPanelProps<{ connectionId: string; key?: string; database?: number }>) {
  const shell = useShell();
  const connection = shell.connections.find(c => c.id === props.params.connectionId);

  return (
    <RedisPanel connectionId={props.params.connectionId}
      readOnly={connection?.readOnly ?? false}
      initialKey={props.params.key} initialDatabase={props.params.database} />
  );
}

function FederationDockPanel() {
  const shell = useShell();
  return <FederationPanel connections={shell.connections} />;
}

function NotebookDockPanel(props: IDockviewPanelProps<{ connectionId?: string }>) {
  const shell = useShell();
  return <NotebookPanel connections={shell.connections} connectionId={props.params.connectionId} />;
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

function ExplorerDockPanel() {
  const { explorer } = useShell();
  const connection = explorer.activeConnection;
  // The administration surface belongs to admins; the server refuses it for anybody else, so
  // offering the button would only be a promise it cannot keep.
  const role = useRole();

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%" }}>
      <Group gap={2} px={4} pt={4} wrap="nowrap">
        <Tooltip label="New query">
          <ActionIcon size="sm" variant="subtle" aria-label="New query" disabled={!connection}
            onClick={explorer.newQuery}>
            <IconSquarePlus size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="History">
          <ActionIcon size="sm" variant="subtle" aria-label="History"
            onClick={() => explorer.focusPanel("history")}>
            <IconHistory size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Diagram">
          <ActionIcon size="sm" variant="subtle" aria-label="Diagram" disabled={!connection}
            onClick={() => explorer.openTool("diagram", "Diagram", connection)}>
            <IconSitemap size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Compare">
          <ActionIcon size="sm" variant="subtle" aria-label="Compare" disabled={!connection}
            onClick={() => explorer.openTool("compare", "Compare", connection)}>
            <IconGitCompare size={15} />
          </ActionIcon>
        </Tooltip>
        {isAdmin(role) ? (
          <Tooltip label="Administration">
            <ActionIcon size="sm" variant="subtle" aria-label="Administration" disabled={!connection}
              onClick={() => explorer.openTool("admin", "Admin", connection)}>
              <IconSettingsCog size={15} />
            </ActionIcon>
          </Tooltip>
        ) : null}
        <Tooltip label="Notebook: SQL, prose and results in one document">
          <ActionIcon size="sm" variant="subtle" aria-label="Notebook"
            onClick={() => explorer.openTool("notebook", "Notebook", connection)}>
            <IconNotebook size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Join across connections">
          <ActionIcon size="sm" variant="subtle" aria-label="Federated query"
            disabled={!connection}
            onClick={() => explorer.openTool("federate", "Federated", connection)}>
            <IconArrowsJoin size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Query builder">
          <ActionIcon size="sm" variant="subtle" aria-label="Query builder" disabled={!connection}
            onClick={() => explorer.openTool("builder", "Builder", connection)}>
            <IconTable size={15} />
          </ActionIcon>
        </Tooltip>
        {/* Only for Redis: a key browser on PostgreSQL would be a button that cannot work. */}
        {explorer.engineOf(connection) === "redis" ? (
          <Tooltip label="Redis browser">
            <ActionIcon size="sm" variant="subtle" aria-label="Redis browser"
              onClick={() => explorer.openTool("redis", "Redis", connection)}>
              <IconKey size={15} />
            </ActionIcon>
          </Tooltip>
        ) : null}
        <Tooltip label="Saved queries">
          <ActionIcon size="sm" variant="subtle" aria-label="Saved queries"
            onClick={() => explorer.focusPanel("saved")}>
            <IconBookmarks size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Layout presets">
          <ActionIcon size="sm" variant="subtle" aria-label="Layout presets"
            onClick={explorer.openLayouts}>
            <IconLayoutBoard size={15} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label="Command palette (Ctrl+K)">
          <ActionIcon size="sm" variant="subtle" aria-label="Command palette"
            onClick={explorer.openPalette}>
            <IconCommand size={15} />
          </ActionIcon>
        </Tooltip>
      </Group>

      <div style={{ flex: 1, minHeight: 0 }}>
        <ExplorerTree key={explorer.nonce} onSelect={explorer.select} onAction={explorer.action} />
      </div>
    </div>
  );
}

const components = {
  explorer: ExplorerDockPanel,
  structure: StructurePanel, query: QueryPanel, history: HistoryDockPanel, welcome: WelcomePanel,
  data: DataPanel, plan: PlanDockPanel, health: HealthDockPanel, designer: DesignerPanel,
  diagram: DiagramDockPanel, admin: AdminDockPanel, compare: CompareDockPanel,
  saved: SavedQueriesDockPanel, builder: QueryDesignerDockPanel, redis: RedisDockPanel,
  federate: FederationDockPanel, notebook: NotebookDockPanel,
};

/// The default arrangement, in one place: the initial layout and the reset command must produce
/// the same thing, or reset becomes its own surprise.
function buildDefaultLayout(
  api: DockviewApi, centerGroup: React.MutableRefObject<DockviewGroupPanel | null>) {
  const welcome = api.addPanel({ id: "welcome", component: "welcome", title: "Start" });
  centerGroup.current = welcome.group;

  // The explorer is a panel too, so it can be moved or closed like the rest. It is added after the
  // centre group so the editor keeps the middle.
  api.addPanel({
    id: "explorer", component: "explorer", title: "Explorer",
    position: { referencePanel: welcome.id, direction: "left" },
    initialWidth: 280,
  });

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

/// Panels a close-everything action must leave alone: the explorer is the way to everything else,
/// and the start page is what a layout is rebuilt around.
const PROTECTED_PANELS = new Set(["explorer", "welcome"]);

/// The orange border on the group a panel lives in. Restarting the animation needs the class off,
/// a reflow, and the class back on — otherwise activating an already-active panel shows nothing,
/// which is exactly the case this exists for.
function flashPanel(element: HTMLElement | undefined) {
  if (!element) return;
  element.classList.remove("wds-flash");
  void element.offsetWidth;
  element.classList.add("wds-flash");
  window.setTimeout(() => element.classList.remove("wds-flash"), 800);
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
  // Pinned tabs: dockview's own pinning is an enterprise module, and what is useful about it here
  // is that a tab stops being closed by accident — including by "close others".
  const [pinnedPanels, setPinnedPanels] = useState<Set<string>>(new Set());
  // Panels currently living in their own window, so closing that window cannot lose them.
  const poppedOut = useRef(new Map<string, {
    component: string; title: string; params: Record<string, unknown> | undefined;
  }>());
  const chord = useRef(false);
  const chordTimer = useRef(0);
  // Rendered, not just read: the slot numbers only show while a digit would still land.
  const [chordOpen, setChordOpen] = useState(false);
  const [exportTarget, setExportTarget] = useState<ExportTarget | null>(null);
  const [importTarget, setImportTarget] = useState<ImportTarget | null>(null);
  const [copySource, setCopySource] = useState<{ connectionId: string; objectRef: string; label: string } | null>(null);
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [snippetsOpen, setSnippetsOpen] = useState(false);
  const [layoutsOpen, setLayoutsOpen] = useState(false);
  const [explorerNonce, setExplorerNonce] = useState(0);
  const [gotoOpen, setGotoOpen] = useState(false);
  const [indexTarget, setIndexTarget] = useState<
    { connectionId: string; objectRef: string; schema: string; label: string; column?: string } | null>(null);
  const [newDatabase, setNewDatabase] = useState<DatabaseTarget | null>(null);
  const [dropDatabaseTarget, setDropDatabaseTarget] = useState<DatabaseTarget | null>(null);
  const [propertiesFor, setPropertiesFor] = useState<{ connectionId: string; label: string } | null>(null);
  // Held here, not in the modal: the Ctrl+L chord has to reach the same list the modal numbers.
  const { presets, save: savePresets } = useLayoutPresets();

  useEffect(() => { listConnections().then(setConnections).catch(() => setConnections([])); }, []);

  // Every way of reaching a panel goes through here, so the flash is not something half the
  // buttons remember to do.
  const focusPanel = useCallback((id: string) => {
    const panel = api.current?.getPanel(id);
    if (!panel) return;
    panel.api.setActive();
    flashPanel(panel.group.element);
  }, []);

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

    event.api.onDidAddPopoutGroup(popout => {
      // dockview copies the stylesheets into a popout window but not Mantine's colour-scheme
      // attribute, so a panel popped out of a dark studio would open as a white rectangle.
      const source = document.documentElement;
      const target = popout.window.window?.document.documentElement;

      if (target)
        for (const attribute of ["data-mantine-color-scheme", "data-theme"])
          if (source.hasAttribute(attribute))
            target.setAttribute(attribute, source.getAttribute(attribute)!);

      // What each panel out there is, so it can be put back. Closing the window is supposed to
      // re-dock them, and it does not when the group they came from is gone — the panels simply
      // disappear, which for a query tab means losing the tab.
      for (const panel of popout.group.panels)
        poppedOut.current.set(panel.id, {
          component: panel.api.component,
          title: panel.api.title ?? panel.id,
          params: panel.params,
        });
    });

    event.api.onDidRemovePopoutGroup(() => {
      // After the window is gone: whatever did not come back gets rebuilt in the centre group.
      window.setTimeout(() => {
        for (const [id, panel] of poppedOut.current) {
          if (api.current?.getPanel(id)) continue;

          api.current?.addPanel({
            id, component: panel.component, title: panel.title, params: panel.params,
            position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
          });
        }

        poppedOut.current.clear();
      }, 50);
    });
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

  /// The table a column, index, key or trigger node belongs to: its own path without the last part.
  const parentTableRef = useCallback((ref: string) => {
    const parts = ref.split(":", 2)[1]?.split("/") ?? [];
    return `Table:${parts.slice(0, -1).join("/")}`;
  }, []);

  const engineOf = useCallback((connectionId: string) =>
    connections.find(c => c.id === connectionId)?.engine ?? "postgresql", [connections]);

  /// Opens the Redis panel on one key. The ref is `Table:{db}/{part}/{part}`, and the key is those
  /// parts joined by ':' again — the separator the tree split them on.
  const openRedisKey = useCallback((connectionId: string, objectRef: string) => {
    const path = objectRef.split(":").slice(1).join(":").split("/");
    const database = Number(path[0]) || 0;
    const key = path.slice(1).join(":");
    const id = `redis:${connectionId}`;

    const existing = api.current?.getPanel(id);
    if (existing) {
      existing.api.updateParameters({ connectionId, key, database });
      focusPanel(id);
      flashPanel(existing.group.element);
      return;
    }

    api.current?.addPanel({
      id, component: "redis", title: "Keys", params: { connectionId, key, database },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
    flashPanel(api.current?.getPanel(id)?.group.element);
  }, [focusPanel]);

  const handleAction = useCallback(async (action: ExplorerAction, s: ExplorerSelection) => {
    const name = qualify(s.connectionId, s.node.ref);
    const engine = engineOf(s.connectionId);
    const schemaOf = (ref: string) => ref.split(":")[1]?.split("/")[0] ?? "";
    const ownerRef = parentTableRef(s.node.ref);
    const ownerName = qualify(s.connectionId, ownerRef);

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
        // A Redis key is one value, not a page of rows: it belongs in the key browser. Asking the
        // data tab for it produced "ERR wrong number of arguments for 'select' command", because
        // `SELECT * FROM key` is not a thing Redis can be asked.
        if (engine === "redis") openRedisKey(s.connectionId, s.node.ref);
        else await openData(s.connectionId, s.node.ref, s.node.label);
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

      case "structure":
        setSelection(s);
        focusPanel("structure");
        break;

      case "copy-link":
        await navigator.clipboard.writeText(window.location.origin + window.location.pathname
          + buildDeepLink({ kind: "object", connectionId: s.connectionId, objectRef: s.node.ref }));
        break;

      // Indexes are edited in the designer, on its index tab — same preview and apply as any
      // other schema change, rather than a second path that bypasses it.
      case "manage-indexes":
        setIndexTarget({
          connectionId: s.connectionId,
          objectRef: s.node.kind === "Table" ? s.node.ref : ownerRef,
          schema: schemaOf(s.node.ref),
          label: s.node.kind === "Table" ? s.node.label : ownerRef.split("/").pop() ?? "",
        });
        break;

      case "add-index":
        setIndexTarget({
          connectionId: s.connectionId,
          objectRef: ownerRef,
          schema: schemaOf(s.node.ref),
          label: ownerRef.split("/").pop() ?? "",
          column: s.node.label,
        });
        break;

      case "script-select-column":
        newTab(s.connectionId, selectColumn(engine, ownerName, s.node.label));
        break;

      case "script-drop-column":
        newTab(s.connectionId, dropColumn(engine, ownerName, s.node.label));
        break;

      case "script-drop-index":
        newTab(s.connectionId, dropIndex(engine, ownerName, s.node.label));
        break;

      case "script-reindex":
        newTab(s.connectionId, rebuildIndex(engine, ownerName, s.node.label));
        break;

      case "script-drop-constraint":
        newTab(s.connectionId, dropConstraint(engine, ownerName, s.node.label));
        break;

      case "script-execute":
        newTab(s.connectionId, executeRoutine(engine, name));
        break;

      case "script-refresh-matview":
        newTab(s.connectionId, refreshMaterializedView(engine, name));
        break;

      case "new-database":
        setNewDatabase({ connectionId: s.connectionId });
        break;

      case "drop-database":
        setDropDatabaseTarget({ connectionId: s.connectionId, name: s.node.label });
        break;

      case "properties":
        setPropertiesFor({
          connectionId: s.connectionId,
          label: connections.find(c => c.id === s.connectionId)?.name ?? s.node.label,
        });
        break;

      default:
        setSelection(s);
    }
  }, [focusPanel, newTab, openData, openDesigner, openRedisKey, qualify, engineOf, parentTableRef,
    connections]);

  const runStatement = useCallback((connectionId: string, sql: string) => newTab(connectionId, sql), [newTab]);

  const exportQuery = useCallback((connectionId: string, sql: string) =>
    setExportTarget({ connectionId, sql, defaultName: "result", scopes: ["result"] }), []);

  const exportObject = useCallback((connectionId: string, objectRef: string, label: string) =>
    setExportTarget({
      connectionId, objectRef,
      schema: objectRef.split(":", 2)[1]?.split("/")[0],
      defaultName: label,
      scopes: ["table", "schema"],
    }), []);

  const dialectOf = useCallback((connectionId: string) =>
    dialectFor(connections.find(c => c.id === connectionId)?.engine ?? "postgresql"), [connections]);

  const activeConnection = selection?.connectionId ?? connections[0]?.id ?? "";

  // One panel per tool and connection: clicking the button again focuses the panel that is
  // already open instead of stacking duplicates.
  const openTool = useCallback((component: string, title: string, connectionId: string) => {
    if (!connectionId) return;
    const id = `${component}:${connectionId}`;
    if (api.current?.getPanel(id)) { focusPanel(id); return; }

    api.current?.addPanel({
      id, component, title, params: { connectionId },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
    flashPanel(api.current?.getPanel(id)?.group.element);
  }, [focusPanel]);

  // The explorer can be closed like any other panel, so there has to be a way back that is not
  // "reset everything".
  const showExplorer = useCallback(() => {
    if (api.current?.getPanel("explorer")) { focusPanel("explorer"); return; }
    api.current?.addPanel({
      id: "explorer", component: "explorer", title: "Explorer",
      position: centerGroup.current
        ? { referenceGroup: centerGroup.current, direction: "left" }
        : undefined,
      initialWidth: 280,
    });
    flashPanel(api.current?.getPanel("explorer")?.group.element);
  }, [focusPanel]);

  const resetLayout = useCallback(() => {
    // The way back from a layout with every panel closed: rebuild the default arrangement.
    api.current?.clear();
    if (api.current) buildDefaultLayout(api.current, centerGroup);
  }, []);

  const applyLayout = useCallback((layout: unknown) => {
    try {
      api.current?.fromJSON(layout as never);
      // The old centre group died with the layout; new query tabs would otherwise land nowhere.
      centerGroup.current = api.current?.getPanel("welcome")?.group
        ?? api.current?.groups[0] ?? null;
    } catch {
      resetLayout();
    }
  }, [resetLayout]);

  // "Open this query in the builder" for a statement the builder itself generated. A fresh panel
  // per query, because the model replaces whatever the builder currently holds.
  const openInBuilder = useCallback((connectionId: string, sql: string) => {
    if (!connectionId) return;
    const id = `builder:${connectionId}:${Date.now().toString(36)}`;

    api.current?.addPanel({
      id, component: "builder", title: "Builder", params: { connectionId, sql },
      position: centerGroup.current ? { referenceGroup: centerGroup.current } : undefined,
    });
    flashPanel(api.current?.getPanel(id)?.group.element);
  }, []);

  const pins = useMemo<TabPins>(() => ({
    isPinned: id => pinnedPanels.has(id),
    isProtected: id => PROTECTED_PANELS.has(id),
    togglePinned: id => setPinnedPanels(current => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    }),
  }), [pinnedPanels]);

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
    openHealth: () => focusPanel("health"),
    openAdmin: () => openTool("admin", "Admin", activeConnection),
    openCompare: () => openTool("compare", "Compare", activeConnection),
    openNotebook: () => openTool("notebook", "Notebook", activeConnection),
    openFederation: () => openTool("federate", "Federated", activeConnection),
    openHistory: () => focusPanel("history"),
    openSavedQueries: () => focusPanel("saved"),
    saveCurrentQuery: () => focusPanel("saved"),
    exportResult: () => {
      const tab = tabs[tabs.length - 1];
      if (tab) exportQuery(tab.connectionId, tab.sql);
    },
    openSnippets: () => setSnippetsOpen(true),
    showExplorer,
    openInBuilder: () => {
      const tab = tabs[tabs.length - 1];
      if (tab) openInBuilder(tab.connectionId, tab.sql);
    },
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
  }), [activeConnection, focusPanel, newTab, openInBuilder, openTool, resetLayout, selection, showExplorer, tabs, exportQuery]);

  // Ctrl+K everywhere, "?" only outside a text field — otherwise it eats a question mark.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target?.closest("input, textarea, .monaco-editor") !== null;

      // Ctrl+L opens the preset list and arms the chord: the digit that follows picks a layout, 0
      // resets. A chord keeps Ctrl+1…9 free, which the browser owns for its tabs.
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "l") {
        event.preventDefault();
        setLayoutsOpen(true);
        window.clearTimeout(chordTimer.current);
        chord.current = true;
        setChordOpen(true);
        chordTimer.current = window.setTimeout(() => {
          chord.current = false;
          setChordOpen(false);
        }, 3000);
        return;
      }
      // A digit typed into a field is part of a name, not a slot — the preset dialog has a text
      // input right there.
      if (chord.current && !typing && /^[0-9]$/.test(event.key)) {
        event.preventDefault();
        chord.current = false;
        setChordOpen(false);
        setLayoutsOpen(false);

        const slot = Number(event.key);
        if (slot === 0) { resetLayout(); return; }

        const preset = presetForSlot(presets, activeConnection || null, slot);
        if (preset) applyLayout(preset.layout);
        return;
      }

      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setPaletteOpen(true);
        return;
      }
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "b") {
        event.preventDefault();
        showExplorer();
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
  }, [activeConnection, applyLayout, presets, resetLayout, showExplorer]);

  // The header button lives outside this component; the theme switch uses the same channel.
  useEffect(() => {
    const open = () => setLayoutsOpen(true);
    document.addEventListener("wds:layouts", open);
    return () => document.removeEventListener("wds:layouts", open);
  }, []);

  // The chat lives outside the dock, so "put this in the editor" arrives as an event.
  useEffect(() => {
    const use = (event: Event) => {
      const sql = (event as CustomEvent<string>).detail;
      if (typeof sql === "string" && sql.trim().length > 0) newTab(activeConnection, sql);
    };

    document.addEventListener("wds:use-sql", use);
    return () => document.removeEventListener("wds:use-sql", use);
  }, [activeConnection, newTab]);

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
    <ShellContext.Provider value={{
      connections,
      selection, tabs, dataTabs, designerTabs, updateSql, openObject, exportQuery, followForeignKey,
      runStatement, openData, dialectOf, exportObject,
      explorer: {
        nonce: explorerNonce,
        activeConnection,
        select: setSelection,
        action: handleAction,
        newQuery: () => newTab(activeConnection),
        focusPanel,
        openTool,
        openLayouts: () => setLayoutsOpen(true),
        openPalette: () => setPaletteOpen(true),
        engineOf: id => connections.find(c => c.id === id)?.engine ?? "",
      },
    }}>
      <TabPinsProvider value={pins}>
      <div style={{ height: "100%", minHeight: 0 }}>
        <DockviewReact className={current.dockview} components={components} onReady={onReady}
          // Our own tab: dockview's getTabContextMenuItems and pinnedTabs are enterprise modules,
          // and the community core ignores both options without erroring.
          defaultTabComponent={StudioTab} />
      </div>
      </TabPinsProvider>
      <VersionBadge />

      <ExportDialog target={exportTarget} onClose={() => setExportTarget(null)} />
      <ImportDialog target={importTarget} onClose={() => setImportTarget(null)} />
      <CopyTableDialog source={copySource} connections={connections}
        onClose={() => setCopySource(null)} />

      <Modal opened={indexTarget !== null} onClose={() => setIndexTarget(null)} size="xl"
        title={indexTarget ? `Indexes of ${indexTarget.label}` : ""}>
        {indexTarget ? (
          <div style={{ height: 460 }}>
            <TableDesigner connectionId={indexTarget.connectionId} objectRef={indexTarget.objectRef}
              schema={indexTarget.schema} focus="indexes" seedIndexColumn={indexTarget.column}
              onSaved={() => setIndexTarget(null)} />
          </div>
        ) : null}
      </Modal>

      <PropertiesDialog connectionId={propertiesFor?.connectionId ?? null}
        label={propertiesFor?.label ?? ""} onClose={() => setPropertiesFor(null)} />

      <NewDatabaseDialog target={newDatabase} onClose={() => setNewDatabase(null)}
        onDone={() => setExplorerNonce(n => n + 1)} />
      <DropDatabaseDialog target={dropDatabaseTarget} onClose={() => setDropDatabaseTarget(null)}
        onDone={() => setExplorerNonce(n => n + 1)} />

      <GoToObject connectionId={activeConnection} opened={gotoOpen} onClose={() => setGotoOpen(false)}
        onPick={table => openData(activeConnection, table.ref, table.name)} />
      <CommandPalette commands={commands} opened={paletteOpen} onClose={() => setPaletteOpen(false)} />
      <ShortcutsHelp commands={commands} opened={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
      <SnippetManager opened={snippetsOpen} onClose={() => setSnippetsOpen(false)} />
      <LayoutPresetsModal opened={layoutsOpen} onClose={() => setLayoutsOpen(false)}
        connectionId={activeConnection || null}
        presets={presets} save={savePresets} slotsArmed={chordOpen}
        capture={() => api.current?.toJSON()}
        apply={applyLayout}
        reset={resetLayout} />
    </ShellContext.Provider>
  );
}
