import { useEffect, useState } from "react";
import { ActionIcon, Badge, Divider, Menu, Text, Tooltip } from "@mantine/core";
import { IconLogout, IconUser } from "@tabler/icons-react";
import { logout, me, type Me } from "../api";

const roleColour = (role?: string | null) =>
  role === "admin" ? "red" : role === "editor" ? "blue" : "gray";

const roleExplanation: Record<string, string> = {
  admin: "everything, including the administration panel",
  editor: "read and write, but not administer",
  viewer: "read-only, on the connections assigned to you",
};

/// Who is signed in, and the way out. Absent on a studio without accounts: there is nobody to be,
/// and a logout button that cannot log anybody out is worse than no button.
export function UserMenu() {
  const [state, setState] = useState<Me | null>(null);

  useEffect(() => { me().then(setState).catch(() => setState(null)); }, []);

  if (!state || state.anonymous || !state.username) return null;

  const role = state.role ?? null;

  return (
    <Menu withinPortal position="bottom-end" width={260}>
      <Menu.Target>
        <Tooltip label={`Signed in as ${state.username}`}>
          <ActionIcon variant="subtle" aria-label="Account">
            <IconUser size={18} />
          </ActionIcon>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Signed in</Menu.Label>
        <Menu.Item component="div" style={{ cursor: "default" }}>
          <Text size="sm" fw={600}>{state.username}</Text>
          {role ? (
            <>
              <Badge size="xs" variant="light" color={roleColour(role)} mt={4}>{role}</Badge>
              <Text size="xs" c="dimmed" mt={4}>{roleExplanation[role] ?? ""}</Text>
            </>
          ) : null}
        </Menu.Item>

        <Divider />
        {/* Accounts come from the environment: a rollout is the only way to change them, which is
            why this says where to look instead of offering an editor that could not work. */}
        <Menu.Label>Accounts live in WDS_USERS</Menu.Label>
        <Menu.Item component="a" href="https://fgilde.github.io/WebDataStudio/guide/#/safety"
          target="_blank" rel="noreferrer">
          <Text size="xs">How to add users and roles</Text>
        </Menu.Item>

        <Divider />
        <Menu.Item color="red" leftSection={<IconLogout size={14} />}
          onClick={async () => { await logout(); window.location.reload(); }}>
          Sign out
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
