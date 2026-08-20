import { useEffect, useRef, useState } from "react";

/// A ring buffer of samples, polled on an interval. What the overview needs and nothing more: the
/// last N readings of whatever the caller measures, kept so the tiles can draw a line rather than a
/// number that jumps.
export function useMetricHistory<T>(
  sample: () => Promise<T>, intervalMs: number, keep: number,
): { samples: T[]; latest: T | null; error: string | null } {
  const [samples, setSamples] = useState<T[]>([]);
  const [error, setError] = useState<string | null>(null);
  const sampleRef = useRef(sample);

  useEffect(() => { sampleRef.current = sample; }, [sample]);

  useEffect(() => {
    let cancelled = false;

    const take = async () => {
      try {
        const value = await sampleRef.current();
        if (cancelled) return;

        // A failed sample must not wipe the history: a blip in the middle of an incident is
        // exactly when the previous readings matter.
        setSamples(current => [...current, value].slice(-keep));
        setError(null);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    };

    void take();
    const timer = window.setInterval(take, intervalMs);

    return () => { cancelled = true; window.clearInterval(timer); };
  }, [intervalMs, keep]);

  return { samples, latest: samples.length > 0 ? samples[samples.length - 1] : null, error };
}

/// The path of a sparkline over a series, normalised into the box. Flat data draws a flat line
/// rather than dividing by zero.
export function sparklinePath(values: number[], width: number, height: number): string {
  if (values.length === 0) return "";
  if (values.length === 1) return `M0,${height / 2} L${width},${height / 2}`;

  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const step = width / (values.length - 1);

  return values
    .map((value, index) => {
      const x = index * step;
      const y = height - ((value - min) / span) * height;
      return `${index === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}
