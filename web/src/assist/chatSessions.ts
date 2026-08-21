import { loadWorkspaceItem, saveWorkspaceItem } from "../api";

export interface ChatMessage {
  role: "user" | "assistant";
  content: string;
  /// Tools the assistant used for this answer, when it used any.
  usedTools?: string[];
  /// SQL the answer contained, so it can be put into the editor.
  statements?: string[];
}

export interface ChatSession {
  id: string;
  title: string;
  connectionId: string | null;
  at: string;
  messages: ChatMessage[];
}

const INDEX = "chat-sessions";
const key = (id: string) => `chat:${id}`;

/// How many sessions are kept. Enough to come back to yesterday's question, few enough that the
/// workspace row stays small.
export const MAX_SESSIONS = 20;

export interface SessionStub { id: string; title: string; at: string }

/// A title from the first thing that was asked, because "New chat" tells nobody anything.
export function titleOf(messages: ChatMessage[]): string {
  const first = messages.find(m => m.role === "user")?.content.trim() ?? "";
  if (first.length === 0) return "New chat";

  const line = first.split("\n")[0];
  return line.length <= 48 ? line : `${line.slice(0, 47)}…`;
}

export const newSession = (connectionId: string | null): ChatSession => ({
  // Random rather than time-based: two tabs starting a chat in the same millisecond must not
  // overwrite each other.
  id: Math.random().toString(36).slice(2, 10),
  title: "New chat",
  connectionId,
  at: new Date().toISOString(),
  messages: [],
});

export const listSessions = (): Promise<SessionStub[]> =>
  loadWorkspaceItem<SessionStub[]>(INDEX).then(stored => stored ?? []).catch(() => []);

export const loadSession = (id: string): Promise<ChatSession | null> =>
  loadWorkspaceItem<ChatSession>(key(id)).catch(() => null);

/// Saves the session and keeps the index in front of it. Best-effort: a chat is worth keeping, but
/// not worth failing a conversation over.
export async function saveSession(session: ChatSession): Promise<SessionStub[]> {
  const stub: SessionStub = {
    id: session.id,
    title: titleOf(session.messages),
    at: new Date().toISOString(),
  };

  const index = [stub, ...(await listSessions()).filter(s => s.id !== session.id)]
    .slice(0, MAX_SESSIONS);

  await saveWorkspaceItem(key(session.id), { ...session, title: stub.title, at: stub.at });
  await saveWorkspaceItem(INDEX, index);

  return index;
}

export async function deleteSession(id: string): Promise<SessionStub[]> {
  const index = (await listSessions()).filter(s => s.id !== id);

  // The session body is left to expire with the workspace: there is no delete for a workspace
  // item, and an entry nothing points at is invisible.
  await saveWorkspaceItem(INDEX, index);
  return index;
}
