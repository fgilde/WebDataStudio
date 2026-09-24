import type { AnalyzeResultDto } from "../api";

/// Opened plan files, by tab. In memory only: a plan can be megabytes, and the dock's saved layout
/// lives in local storage, which would take the whole plan with every layout change.
const files = new Map<string, AnalyzeResultDto>();
let next = 0;

export const planFiles = {
  put(result: AnalyzeResultDto): string {
    const id = `planfile-${Date.now().toString(36)}-${next++}`;
    files.set(id, result);
    return id;
  },
  get: (id: string) => files.get(id),
  drop: (id: string) => { files.delete(id); },
};
