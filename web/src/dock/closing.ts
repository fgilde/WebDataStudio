/// Which tabs a "close everything" actually closes.
///
/// The panels the studio starts with — the explorer, the structure and plan side, the history and
/// saved lists, the start page — are the layout, not somebody's work. Closing all tabs meant
/// rebuilding the window afterwards, which is not what anybody asks for when they close a dozen
/// query tabs. Only what was opened during the session goes.

export type CloseScope = "others" | "right" | "all";

export interface PanelFacts {
  id: string;
  /// Pinned by hand: "keep this open" is an answer to this question too.
  pinned: boolean;
  /// Part of the arrangement the studio builds itself, rather than something opened during the
  /// session.
  layout: boolean;
}

/// The ids to close, given the tabs in play and which one the menu was opened on.
///
/// `right` counts the order of the tabs it is given, which is the group's own panel order; the
/// others look at everything in the window.
export function panelsToClose(scope: CloseScope, panels: PanelFacts[], current: string): string[] {
  const keep = (panel: PanelFacts) => panel.pinned || panel.layout;

  if (scope === "right") {
    const index = panels.findIndex(panel => panel.id === current);
    if (index < 0) return [];

    return panels.slice(index + 1).filter(panel => !keep(panel)).map(panel => panel.id);
  }

  return panels
    .filter(panel => !keep(panel) && (scope === "all" || panel.id !== current))
    .map(panel => panel.id);
}
