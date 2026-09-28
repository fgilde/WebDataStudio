import { useEffect, useState } from "react";
import {
  ActionIcon, Alert, Button, Group, Loader, Modal, ScrollArea, Stack, Text, TextInput, Tooltip,
  UnstyledButton,
} from "@mantine/core";
import { IconArrowUp, IconDatabase, IconFile, IconFolder, IconFolderPlus } from "@tabler/icons-react";
import { browseFiles, makeServerFolder, type BrowseDto } from "../api";
import { isAdmin, useRole } from "../auth/useRole";

/// The folders the server can see, walked from its roots. In "file" mode a file is the answer; in
/// "folder" mode the folder that is open is, and a new one can be made inside it first.
export function ServerBrowser({ opened, onClose, mode, start, onPickFile, onPickFolder }: {
  opened: boolean;
  onClose: () => void;
  mode: "file" | "folder";
  /// Where to open, when there is a path already — the field the picker fills in, say.
  start?: string;
  onPickFile?: (path: string, engine: string) => void;
  onPickFolder?: (path: string) => void;
}) {
  const role = useRole();
  const [page, setPage] = useState<BrowseDto | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [newFolder, setNewFolder] = useState<string | null>(null);

  const go = async (path?: string) => {
    setProblem(null);
    setNewFolder(null);
    try {
      setPage(await browseFiles(path));
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  useEffect(() => {
    if (!opened) return;
    setPage(null);
    // A path from the field that is gone or outside the roots starts at the roots instead.
    (start?.trim() ? browseFiles(start.trim()).catch(() => browseFiles()) : browseFiles())
      .then(setPage)
      .catch(e => setProblem(e instanceof Error ? e.message : String(e)));
    // Only on opening: `start` follows the field, and typing there must not move the browser.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened]);

  const make = async () => {
    if (!page?.path || !newFolder?.trim()) return;
    try {
      const made = await makeServerFolder(page.path, newFolder.trim());
      await go(made.path);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  const row = (icon: React.ReactNode, label: string, onClick?: () => void, hint?: string) => (
    <UnstyledButton key={label} disabled={!onClick} onClick={onClick}>
      <Group gap={6} wrap="nowrap">
        {icon}
        <Text size="sm" c={onClick ? undefined : "dimmed"}>{label}</Text>
        {hint && <Text size="xs" c="dimmed">{hint}</Text>}
      </Group>
    </UnstyledButton>
  );

  return (
    <Modal opened={opened} onClose={onClose} size="lg"
      title={mode === "folder" ? "Pick a folder on the server" : "Files the server can see"}>
      <Stack gap={4}>
        {problem && <Alert color="red" variant="light">{problem}</Alert>}

        {page === null ? <Loader size="sm" /> : page.path === null
          ? page.roots.map(root => row(<IconFolder size={16} />, root, () => void go(root)))
          : (
            <>
              <Group justify="space-between" wrap="nowrap">
                <Text size="xs" c="dimmed" truncate>{page.path}</Text>
                {mode === "folder" && isAdmin(role) && (
                  <Tooltip label="New folder here">
                    <ActionIcon size="sm" variant="subtle" aria-label="New folder"
                      onClick={() => setNewFolder("")}>
                      <IconFolderPlus size={15} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>

              {newFolder !== null && (
                <TextInput size="xs" autoFocus placeholder="folder name"
                  description="Enter makes it and opens it, Escape drops it" value={newFolder}
                  onChange={e => setNewFolder(e.currentTarget.value)}
                  onKeyDown={e => {
                    if (e.key === "Enter") { e.preventDefault(); void make(); }
                    if (e.key === "Escape") { e.stopPropagation(); setNewFolder(null); }
                  }} />
              )}

              {/* The roots are the way back from the top of one, so "up" never runs out. */}
              {row(<IconArrowUp size={16} />, "up", () => void go(page.parent ?? undefined))}

              <ScrollArea.Autosize mah={360}>
                <Stack gap={2}>
                  {page.directories.map(directory =>
                    row(<IconFolder size={16} />, directory.name, () => void go(directory.path)))}

                  {/* A file nothing opens is shown rather than hidden — and not pickable, so the
                      answer to "why can I not choose it" is that it is greyed out. In folder mode
                      the files only say what is in the folder. */}
                  {page.files.map(file => row(
                    file.engine === null ? <IconFile size={16} /> : <IconDatabase size={16} />,
                    file.name,
                    mode === "file" && file.engine !== null
                      ? () => { onPickFile?.(file.path, file.engine!); onClose(); }
                      : undefined,
                    mode === "file" ? file.engine ?? "nothing here opens this" : undefined))}
                </Stack>
              </ScrollArea.Autosize>

              {mode === "folder" && (
                <Group justify="flex-end" mt="xs">
                  <Button size="xs" variant="default" onClick={onClose}>Cancel</Button>
                  <Button size="xs" onClick={() => { onPickFolder?.(page.path!); onClose(); }}>
                    Use this folder
                  </Button>
                </Group>
              )}
            </>
          )}
      </Stack>
    </Modal>
  );
}
