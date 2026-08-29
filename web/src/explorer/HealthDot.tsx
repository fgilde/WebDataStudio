import { useEffect, useRef, useState } from "react";
import { Tooltip } from "@mantine/core";
import { checkConnectionHealth, type ConnectionHealthDto } from "../api";

/// Is this server still there? Today you find out by clicking something and waiting for it to fail.
///
/// Deliberately not a poll of every connection every minute: a studio with ten of them would open
/// ten connections, some through an SSH tunnel, for a dot. It checks once when the connection is
/// expanded — the moment somebody shows interest in it — and again whenever the dot is clicked.
export function HealthDot({ id, auto }: { id: string; auto: boolean }) {
  const [state, setState] = useState<ConnectionHealthDto | null>(null);
  const [checking, setChecking] = useState(false);
  const alive = useRef(true);

  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; };
  }, []);

  const check = () => {
    setChecking(true);

    checkConnectionHealth(id)
      .then(result => { if (alive.current) setState(result); })
      .catch(e => {
        if (alive.current) {
          setState({ ok: false, milliseconds: 0, message: e instanceof Error ? e.message : String(e) });
        }
      })
      .finally(() => { if (alive.current) setChecking(false); });
  };

  // Once, when it is expanded. Collapsing and expanding again does not ask twice: what it said is
  // still the most recent thing known, and a stale reading is labelled as one.
  useEffect(() => {
    if (auto && state === null && !checking) check();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auto]);

  const colour = checking
    ? "var(--mantine-color-blue-5)"
    : state === null ? "var(--mantine-color-gray-5)"
      : state.ok ? "var(--mantine-color-teal-6)" : "var(--mantine-color-red-6)";

  const label = checking
    ? "checking…"
    : state === null ? "not checked yet — click to check"
      : state.ok ? `answered in ${state.milliseconds} ms — click to check again`
        : state.message;

  return (
    <Tooltip label={label} withinPortal>
      <span
        role="button"
        aria-label={`Connection health: ${label}`}
        onClick={event => { event.stopPropagation(); check(); }}
        style={{
          width: 7,
          height: 7,
          borderRadius: "50%",
          background: colour,
          flex: "none",
          cursor: "pointer",
          // Slow is not broken, and it is worth seeing: a ring around the dot past a quarter of a
          // second, which is where a query stops feeling immediate.
          outline: state?.ok && state.milliseconds > 250
            ? "2px solid var(--mantine-color-yellow-5)"
            : undefined,
        }} />
    </Tooltip>
  );
}
