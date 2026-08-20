import { useEffect, useState } from "react";
import {
  ActionIcon, Badge, Button, Card, Code, Group, Loader, Modal, ScrollArea, SegmentedControl, Stack,
  Tabs, Text, Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconCopy, IconPlayerPlay, IconRefresh } from "@tabler/icons-react";
import {
  analyzeQuery, applyScript, previewScript,
  type AnalyzeResultDto, type DdlPreviewDto, type PlanNodeDto,
} from "../api";
import { heatColor } from "./heat";

export function PlanPanel({ connectionId, sql, onRunStatement }: {
  connectionId: string;
  sql: string;
  onRunStatement?: (statement: string) => void;
}) {
  const [mode, setMode] = useState<"estimated" | "actual">("estimated");
  const [result, setResult] = useState<AnalyzeResultDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = async (next = mode) => {
    if (!sql.trim()) return;
    setBusy(true);
    setError(null);
    try { setResult(await analyzeQuery(connectionId, sql, next === "actual")); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  useEffect(() => { setResult(null); }, [sql]);

  const maxCost = result?.summary?.maxNodeCost ?? 0;

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4} wrap="nowrap">
        <SegmentedControl size="xs" value={mode} data={[
          { label: "Estimated", value: "estimated" },
          { label: "Actual", value: "actual" },
        ]} onChange={v => { setMode(v as "estimated" | "actual"); load(v as "estimated" | "actual"); }} />
        <Tooltip label={mode === "actual" ? "Actual plans execute the statement" : "Explain without running"}>
          <ActionIcon size="sm" variant="subtle" aria-label="Explain" loading={busy} onClick={() => load()}>
            <IconRefresh size={14} />
          </ActionIcon>
        </Tooltip>
        {result?.summary && (
          <Text size="xs" c="dimmed">
            {result.summary.nodeCount} nodes · total cost {result.summary.totalCost?.toFixed(1) ?? "?"}
          </Text>
        )}
      </Group>

      {mode === "actual" && (
        <Text size="10px" c="orange" px={6}>
          An actual plan runs the statement. Do not use it on a write you have not reviewed.
        </Text>
      )}

      {error && <Text size="xs" c="red" p="xs">{error}</Text>}
      {result?.planError && <Text size="xs" c="orange" p="xs">Plan unavailable: {result.planError}</Text>}

      {!result && !busy && <Text size="xs" c="dimmed" p="xs">Press explain to analyse this statement.</Text>}
      {busy && !result && <Loader size="xs" m="xs" />}

      {result && (
        <Tabs defaultValue="tree" style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}>
          <Tabs.List>
            <Tabs.Tab value="tree">Tree</Tabs.Tab>
            <Tabs.Tab value="findings">
              Findings
              {result.findings.length > 0 && (
                <Badge size="xs" ml={4} variant="light">{result.findings.length}</Badge>
              )}
            </Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="tree" style={{ flex: 1, minHeight: 0 }}>
            <ScrollArea h="100%">
              {result.plan
                ? <PlanTree node={result.plan} maxCost={maxCost} depth={0} />
                : <Text size="xs" c="dimmed" p="xs">This engine returned no plan.</Text>}
            </ScrollArea>
          </Tabs.Panel>

          <Tabs.Panel value="findings" style={{ flex: 1, minHeight: 0 }}>
            <ScrollArea h="100%">
              <Stack gap={6} p={6}>
                {result.findings.length === 0 && <Text size="xs" c="dimmed">Nothing to report.</Text>}
                {result.findings.map((f, i) => (
                  <Card key={i} withBorder padding={8}>
                    <Group gap={6} mb={2}>
                      <Badge size="xs" color={f.severity === "warning" ? "orange" : "gray"} variant="light">
                        {f.severity}
                      </Badge>
                      <Text size="xs" fw={600}>{f.title}</Text>
                    </Group>
                    <Text size="xs" c="dimmed">{f.detail}</Text>
                    {f.statement && (
                      <Group gap={4} mt={6}>
                        <Text size="xs" ff="monospace" style={{ flex: 1, wordBreak: "break-all" }}>
                          {f.statement}
                        </Text>
                        <Tooltip label="Copy">
                          <ActionIcon size="xs" variant="subtle" aria-label="Copy statement"
                            onClick={() => navigator.clipboard.writeText(f.statement!)}>
                            <IconCopy size={12} />
                          </ActionIcon>
                        </Tooltip>
                        {onRunStatement && (
                          <Tooltip label="Open in a new query tab">
                            <ActionIcon size="xs" variant="subtle" aria-label="Run statement"
                              onClick={() => onRunStatement(f.statement!)}>
                              <IconPlayerPlay size={12} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Group>
                    )}
                  </Card>
                ))}
              </Stack>
            </ScrollArea>
          </Tabs.Panel>
        </Tabs>
      )}
    </div>
  );
}

function PlanTree({ node, maxCost, depth }: { node: PlanNodeDto; maxCost: number; depth: number }) {
  return (
    <div>
      <Group gap={6} wrap="nowrap" px={6} py={2}
        style={{ paddingLeft: depth * 14 + 6, background: heatColor(node.estimatedCost ?? 0, maxCost) }}>
        <Text size="xs" fw={600}>{node.operation}</Text>
        {node.detail && <Text size="xs" c="dimmed">{node.detail}</Text>}
        {node.estimatedRows !== null && (
          <Text size="10px" c="dimmed">rows ~{Math.round(node.estimatedRows).toLocaleString()}</Text>
        )}
        {node.actualRows !== null && (
          <Text size="10px" c="dimmed">actual {Math.round(node.actualRows).toLocaleString()}</Text>
        )}
        {node.estimatedCost !== null && <Text size="10px" c="dimmed">cost {node.estimatedCost.toFixed(1)}</Text>}
        {node.actualMs !== null && <Text size="10px" c="dimmed">{node.actualMs.toFixed(2)} ms</Text>}
        {node.warnings.length > 0 && (
          <Tooltip label={node.warnings.join("; ")}>
            <IconAlertTriangle size={12} color="var(--mantine-color-orange-6)" />
          </Tooltip>
        )}
      </Group>
      {node.children.map((child, i) => (
        <PlanTree key={i} node={child} maxCost={maxCost} depth={depth + 1} />
      ))}
    </div>
  );
}

/// The whole-connection report: the same findings, grouped by category.
export function HealthReportPanel({ connectionId, schema }: { connectionId: string; schema?: string }) {
  const [findings, setFindings] = useState<AnalyzeResultDto["findings"] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [pendingFix, setPendingFix] = useState<string | null>(null);
  const [preview, setPreview] = useState<DdlPreviewDto | null>(null);

  // Preview first, run second — the same handshake every other write in this studio goes through.
  useEffect(() => {
    if (pendingFix === null) { setPreview(null); return; }

    previewScript(connectionId, pendingFix)
      .then(setPreview)
      .catch((e: Error) => { setError(e.message); setPendingFix(null); });
  }, [pendingFix, connectionId]);

  const load = async () => {
    setBusy(true);
    setError(null);
    try {
      const query = schema ? `?schema=${encodeURIComponent(schema)}` : "";
      const response = await fetch(`/api/analyze/${connectionId}${query}`);
      if (!response.ok) throw new Error((await response.json()).message ?? "analysis failed");
      setFindings((await response.json()).findings);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [connectionId, schema]);

  const groups = (findings ?? []).reduce<Record<string, typeof findings>>((acc, f) => {
    (acc[f!.category] ??= []).push(f!);
    return acc;
  }, {});

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <Group gap={6} p={4}>
        <Button size="compact-xs" variant="default" loading={busy} onClick={load}>Re-run</Button>
        {findings && <Text size="xs" c="dimmed">{findings.length} findings</Text>}
      </Group>

      {error && <Text size="xs" c="red" p="xs">{error}</Text>}

      <ScrollArea style={{ flex: 1 }}>
        <Stack gap={8} p={6}>
          {Object.entries(groups).map(([category, items]) => (
            <div key={category}>
              <Group gap={6} mb={4}>
                <Text size="xs" fw={700}>{category}</Text>
                <Badge size="xs" variant="light">{items!.length}</Badge>
              </Group>
              <Stack gap={4}>
                {items!.map((f, i) => (
                  <Card key={i} withBorder padding={6}>
                    <Text size="xs" fw={600}>{f.title}</Text>
                    <Text size="xs" c="dimmed">{f.detail}</Text>
                    {f.statement && (
                      <>
                        <Text size="xs" ff="monospace" mt={4} style={{ wordBreak: "break-all" }}>
                          {f.statement}
                        </Text>
                        {/* A finding that names its fix should be able to run it: through the
                            migration preview, which is the same path the table designer uses. */}
                        <Group gap={4} mt={4}>
                          <Button size="compact-xs" variant="default"
                            onClick={() => setPendingFix(f.statement!)}>
                            Apply this…
                          </Button>
                          <ActionIcon size="sm" variant="subtle" aria-label="Copy statement"
                            onClick={() => navigator.clipboard.writeText(f.statement!)}>
                            <IconCopy size={13} />
                          </ActionIcon>
                        </Group>
                      </>
                    )}
                  </Card>
                ))}
              </Stack>
            </div>
          ))}
          {findings?.length === 0 && <Text size="xs" c="dimmed">Nothing to report.</Text>}
        </Stack>
      </ScrollArea>

      <Modal opened={pendingFix !== null} onClose={() => setPendingFix(null)}
        title="Apply this fix?" size="lg">
        <Stack gap="sm">
          {preview?.destructive
            ? <Text size="xs" c="red">This drops something. Read it before you run it.</Text>
            : null}
          <Code block fz="xs">{preview?.script ?? pendingFix}</Code>
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => setPendingFix(null)}>Cancel</Button>
            <Button size="xs" color={preview?.destructive ? "red" : undefined} disabled={!preview}
              onClick={() => {
                if (!preview) return;
                applyScript(connectionId, preview.hash)
                  .then(() => { setPendingFix(null); load(); })
                  .catch((e: Error) => { setError(e.message); setPendingFix(null); });
              }}>
              Run it
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
