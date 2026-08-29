import { createContext, useContext, useState } from "react";
import { DockviewDefaultTab, type IDockviewPanelHeaderProps } from "dockview-react";
import { createPortal } from "react-dom";
import { Text, UnstyledButton } from "@mantine/core";
import { IconPin, IconPinnedOff, IconWindowMaximize } from "@tabler/icons-react";
import { panelsToClose, type CloseScope } from "./closing";

/// The tab strip, with the menu people expect from one.
///
/// dockview 8.1 does have `getTabContextMenuItems` and `pinnedTabs`, but both live in its
/// enterprise modules: the community core logs a missing-module warning and shows nothing. So the
/// menu is ours, built on plain API calls — and "pinned" means the part of pinning that is useful
/// here, a tab that refuses to be closed by its own X or by "close others", rather than a second
/// tab row we cannot render.
export interface TabPins {
  isPinned: (panelId: string) => boolean;
  togglePinned: (panelId: string) => void;
  isProtected: (panelId: string) => boolean;
  /// Part of the arrangement the studio builds itself rather than something opened during the
  /// session. A "close everything" leaves these alone: closing them meant rebuilding the window,
  /// which is not what anybody means by closing a dozen query tabs.
  isLayout: (panelId: string) => boolean;
}

/// Through a context rather than a prop: dockview keeps the tab component it was given on the
/// first render, so a prop captured then would freeze the pin state at "nothing is pinned".
const TabPinsContext = createContext<TabPins>({
  isPinned: () => false,
  togglePinned: () => {},
  isProtected: () => false,
  isLayout: () => false,
});

export const TabPinsProvider = TabPinsContext.Provider;

interface Action {
  label: string;
  icon?: React.ReactNode;
  run: () => void;
  disabled?: boolean;
  divider?: boolean;
}

export function StudioTab(props: IDockviewPanelHeaderProps) {
  const { api, containerApi } = props;
  const pins = useContext(TabPinsContext);
  const [at, setAt] = useState<{ x: number; y: number } | null>(null);

  const pinned = pins.isPinned(api.id);
  const protectedTab = pins.isProtected(api.id) || pinned;
  const poppedOut = api.location.type === "popout";
  const maximized = api.group.api.isMaximized();

  const closable = (id: string) => !pins.isProtected(id) && !pins.isPinned(id);

  /// What a close-everything sees: every tab in the window, or in this group for "to the right",
  /// with the two facts that decide whether it stays.
  const facts = (panels: readonly { id: string }[]) => panels.map(panel => ({
    id: panel.id,
    pinned: pins.isPinned(panel.id),
    layout: pins.isLayout(panel.id) || pins.isProtected(panel.id),
  }));

  const closeMany = (scope: CloseScope, panels: readonly { id: string }[]) => {
    const ids = new Set(panelsToClose(scope, facts(panels), api.id));

    for (const panel of containerApi.panels) if (ids.has(panel.id)) panel.api.close();
  };

  const actions: Action[] = [
    { label: "Close", disabled: !closable(api.id), run: () => api.close() },
    { label: "Close others", run: () => closeMany("others", containerApi.panels) },
    {
      // "To the right" is the order the tabs are in, which is the group's own panel order.
      label: "Close to the right",
      run: () => closeMany("right", api.group.panels),
    },
    { label: "Close all", run: () => closeMany("all", containerApi.panels) },
    {
      label: pinned ? "Unpin" : "Pin — keep it open",
      icon: pinned ? <IconPinnedOff size={13} /> : <IconPin size={13} />,
      run: () => pins.togglePinned(api.id),
      divider: true,
    },
    {
      label: maximized ? "Restore" : "Maximize",
      icon: <IconWindowMaximize size={13} />,
      run: () => (maximized ? api.group.api.exitMaximized() : api.group.api.maximize()),
    },
    poppedOut
      ? {
        label: "Dock back into the studio",
        divider: true,
        run: () => {
          const target = containerApi.groups.find(group => group.api.location.type === "grid");
          if (target) api.moveTo({ group: target });
        },
      }
      : {
        label: "Open in its own window",
        divider: true,
        // A popout is a real browser window, so it needs this click as its user gesture; a blocked
        // popup resolves false and dockview puts the group back where it was.
        run: () => void containerApi.addPopoutGroup(api.group),
      },
  ];

  return (
    <>
      <div onContextMenu={event => {
        event.preventDefault();
        setAt({ x: event.clientX, y: event.clientY });
      }}>
        <DockviewDefaultTab {...props} hideClose={protectedTab} />
      </div>

      {/* A portal straight into the document, positioned at the pointer. Mantine's Popover renders
          `display: none` inside a dockview tab — the tab lives in its own render tree, and the
          floating-ui measurement never resolves there. A fixed div needs no measurement. */}
      {at ? createPortal(
        <>
          <div onMouseDown={() => setAt(null)}
            style={{ position: "fixed", inset: 0, zIndex: 400 }} />

          <div style={{
            position: "fixed", left: at.x, top: at.y, zIndex: 401, minWidth: 210, padding: 4,
            background: "var(--mantine-color-body)", borderRadius: 6,
            border: "1px solid var(--mantine-color-default-border)",
            boxShadow: "var(--mantine-shadow-md)",
          }}>
            {actions.map(action => (
              <div key={action.label}>
                {action.divider ? (
                  <div style={{
                    height: 1, margin: "4px 0",
                    background: "var(--mantine-color-default-border)",
                  }} />
                ) : null}
                <UnstyledButton w="100%" px={8} py={4} disabled={action.disabled}
                  style={{ borderRadius: 4, opacity: action.disabled ? 0.4 : 1 }}
                  onClick={() => {
                    setAt(null);
                    if (!action.disabled) action.run();
                  }}>
                  <Text size="xs">
                    {action.icon ? <span style={{ marginRight: 6 }}>{action.icon}</span> : null}
                    {action.label}
                  </Text>
                </UnstyledButton>
              </div>
            ))}
          </div>
        </>,
        document.body) : null}
    </>
  );
}
