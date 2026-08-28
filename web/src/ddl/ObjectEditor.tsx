import { Button, Group, Modal, Stack, TextInput, Text } from "@mantine/core";
import { IconDeviceFloppy } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { loadDdl, previewRoutine, previewView } from "../api";
import type { PendingScript } from "./ScriptConfirm";
import { QueryEditor } from "../editor/QueryEditor";
import type { DialectId } from "../sql/splitStatements";
import { notifications } from "@mantine/notifications";
import { source, type EditableKind } from "./viewSource";

export type { EditableKind };

/// What the editor was opened on: an object that exists, or a new one in this schema.
export interface ObjectEditorTarget {
  connectionId: string;
  dialect: DialectId;
  kind: EditableKind;
  schema: string;
  /// The object's reference, when it exists. Absent means a new one, and then the name is typed.
  objectRef?: string;
  name?: string;
}

/// A starting point that runs as-is once a name is filled in, rather than an empty box.
const TEMPLATE: Record<EditableKind, (name: string) => string> = {
  view: () => "SELECT *\n  FROM ",
  procedure: name => `CREATE PROCEDURE ${name || "name"}()\nBEGIN\n  \nEND`,
  function: name => `CREATE FUNCTION ${name || "name"}()\nRETURNS int\nAS $$\n  SELECT 1\n$$ LANGUAGE sql`,
  trigger: name => `CREATE TRIGGER ${name || "name"}\nAFTER INSERT ON table_name\nBEGIN\n  \nEND`,
};

/// The source of a view, a routine or a trigger, edited where it lives.
///
/// A view is its SELECT — the studio writes the CREATE around it, because each engine spells
/// "replace this definition" differently. Everything else is the whole statement as the engine
/// hands it over, which is what somebody who wrote a procedure expects to see back.
export function ObjectEditor({ target, onClose, onPreview }: {
  target: ObjectEditorTarget | null;
  onClose: () => void;
  onPreview: (pending: PendingScript) => void;
}) {
  const [name, setName] = useState("");
  const [body, setBody] = useState("");
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!target) return;

    setName(target.name ?? "");
    setBody(TEMPLATE[target.kind](target.name ?? ""));

    if (!target.objectRef) return;

    // An existing object opens with what the engine says it is, not with a guess.
    setLoading(true);
    loadDdl(target.connectionId, target.objectRef)
      .then(loaded => setBody(source(loaded.create ?? "", target.kind)))
      .catch(e => notifications.show({ color: "red", message: e.message }))
      .finally(() => setLoading(false));
  }, [target]);

  const save = async () => {
    if (!target) return;

    const objectName = (target.name ?? name).trim();
    if (objectName.length === 0) {
      notifications.show({ color: "red", message: "this needs a name" });
      return;
    }

    try {
      const preview = target.kind === "view"
        ? await previewView(target.connectionId, target.schema, objectName, body)
        : await previewRoutine(target.connectionId, target.schema, objectName, target.kind, body);

      onPreview({
        connectionId: target.connectionId,
        title: `${target.objectRef ? "Save" : "Create"} ${target.kind} ${objectName}`,
        ...preview,
      });
    } catch (e) {
      notifications.show({ color: "red", message: e instanceof Error ? e.message : String(e) });
    }
  };

  return (
    <Modal opened={target !== null} onClose={onClose} size="80%"
      title={target?.objectRef
        ? `${target.kind} ${target.name ?? ""}`
        : `New ${target?.kind ?? "object"}`}>
      <Stack gap="xs">
        {!target?.objectRef && (
          <TextInput size="xs" label="Name" value={name} data-autofocus
            onChange={event => setName(event.currentTarget.value)} />
        )}

        {target?.kind === "view" && (
          <Text size="xs" c="dimmed">
            The SELECT the view stands for. The studio writes the CREATE around it.
          </Text>
        )}

        <div style={{ height: "50vh", minHeight: 260 }}>
          {target && !loading && (
            <QueryEditor value={body} dialect={target.dialect} connectionId={target.connectionId}
              error={null} onChange={setBody} onRun={save} onRunAll={save} />
          )}
        </div>

        <Group justify="flex-end">
          <Button variant="default" size="xs" onClick={onClose}>Cancel</Button>
          <Button size="xs" leftSection={<IconDeviceFloppy size={14} />} onClick={save}>
            Save…
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
