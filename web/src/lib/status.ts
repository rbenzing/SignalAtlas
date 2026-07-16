// Severity + coverage → reserved STATUS-palette mapping. Status is always
// paired with an icon + text label in the UI, never conveyed by color alone.

import { statusPalette, type StatusKey } from "../theme/palette";

export interface SeverityMeta {
  key: string;
  label: string;
  status: StatusKey;
  color: string;
}

const SEVERITY: Record<string, { label: string; status: StatusKey }> = {
  critical: { label: "Critical", status: "critical" },
  serious: { label: "Serious", status: "serious" },
  high: { label: "Serious", status: "serious" },
  warning: { label: "Warning", status: "warning" },
  medium: { label: "Warning", status: "warning" },
  info: { label: "Info", status: "good" },
  low: { label: "Low", status: "good" },
  good: { label: "Good", status: "good" },
};

/** Resolve a severity string to its status color + label (tolerant fallback). */
export function severityMeta(severity: string): SeverityMeta {
  const key = severity.trim().toLowerCase();
  const m = SEVERITY[key] ?? { label: severity || "Unknown", status: "warning" as StatusKey };
  return { key, label: m.label, status: m.status, color: statusPalette[m.status] };
}

/** Sort order for severities (most severe first). */
const SEVERITY_RANK: Record<StatusKey, number> = {
  critical: 0,
  serious: 1,
  warning: 2,
  good: 3,
};

export function severityRank(status: StatusKey): number {
  return SEVERITY_RANK[status];
}

export type CoverageState = "fresh" | "stale" | "none";

/** Bands older than this (seconds) are considered stale rather than fresh. */
export const COVERAGE_STALE_SECONDS = 300;

export interface CoverageMeta {
  state: CoverageState;
  status: StatusKey;
  label: string;
}

/** Coverage freshness → status key (fresh=good, stale=warning, none=critical). */
export function coverageStatus(state: CoverageState): StatusKey {
  return state === "fresh" ? "good" : state === "stale" ? "warning" : "critical";
}

export const coverageLabel: Record<CoverageState, string> = {
  fresh: "Fresh",
  stale: "Stale",
  none: "No coverage",
};

/** Derive freshness from a band's covered flag + age (in seconds). */
export function coverageMeta(covered: boolean, ageSeconds: number | null): CoverageMeta {
  let state: CoverageState;
  if (!covered || ageSeconds == null) state = "none";
  else if (ageSeconds <= COVERAGE_STALE_SECONDS) state = "fresh";
  else state = "stale";
  return { state, status: coverageStatus(state), label: coverageLabel[state] };
}

/** Human-friendly relative age, e.g. "12m ago", "3h ago", or "—" when unknown. */
export function formatAge(ageSeconds: number | null): string {
  if (ageSeconds == null) return "—";
  if (ageSeconds < 60) return `${Math.round(ageSeconds)}s ago`;
  if (ageSeconds < 3600) return `${Math.round(ageSeconds / 60)}m ago`;
  if (ageSeconds < 86400) return `${Math.round(ageSeconds / 3600)}h ago`;
  return `${Math.round(ageSeconds / 86400)}d ago`;
}
