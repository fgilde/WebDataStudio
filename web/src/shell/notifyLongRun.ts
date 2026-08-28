/// Whether a finished run is worth telling somebody about.
///
/// Two conditions, and both matter: it took long enough that they went and did something else, and
/// the tab is not the one they are looking at. A notification for a query that finished in 40 ms
/// while somebody watched it is noise, and noise gets switched off — along with the notifications
/// that would have been useful.
export function shouldNotify(elapsedMs: number, afterSeconds: number, hidden: boolean): boolean {
  if (afterSeconds <= 0) return false;

  return hidden && elapsedMs >= afterSeconds * 1000;
}

/// What the notification says. Short, because a notification is read in a glance: what happened,
/// how long it took, and — when it failed — that it failed.
export function describeRun(elapsedMs: number, rows: number | null, error: string | null): string {
  const seconds = elapsedMs / 1000;
  const took = seconds < 60
    ? `${seconds.toFixed(1)} s`
    : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`;

  if (error) return `failed after ${took}`;

  return rows === null ? `finished in ${took}` : `${rows} rows in ${took}`;
}

/// Tells the person their long query is done, if the browser lets us and they have not said no.
///
/// Permission is asked for the first time one would actually be sent — never on startup, where a
/// prompt has no context and is refused out of reflex.
export async function notifyLongRun(title: string, body: string): Promise<boolean> {
  if (typeof Notification === "undefined") return false;

  let permission = Notification.permission;

  if (permission === "default") {
    try { permission = await Notification.requestPermission(); }
    catch { return false; }
  }

  if (permission !== "granted") return false;

  try {
    new Notification(title, { body, tag: "wds-query" });
    return true;
  } catch {
    // Some browsers only allow notifications from a service worker. Nothing to do about that here,
    // and it is not worth an error in front of somebody who was not even looking.
    return false;
  }
}
