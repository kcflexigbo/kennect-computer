import { appendFileSync, mkdirSync, readdirSync, unlinkSync } from "node:fs";
import path from "node:path";

import { AUDIT_RETENTION_DAYS } from "../config.js";

/**
 * One persisted audit record. The field set is deliberately small (R38): the
 * tool, the target identifiers, the outcome, and timing — never a window
 * title, message, typed text, or snapshot content. Type actions may record
 * only the UTF-8 byte length and SHA-256 hex digest of the typed text, so an
 * operator can correlate a value without the log holding it.
 */
export interface AuditEntry {
  timestamp: string;
  sessionId: string;
  tool: string;
  windowId?: string;
  process?: string;
  nodeId?: string;
  x?: number;
  y?: number;
  textLength?: number;
  textSha256?: string;
  result: string;
  durationMs: number;
}

/**
 * The caller supplies everything except the timestamp; the log stamps it from
 * its injectable clock so a test can pin the UTC day file.
 */
export type AuditEntryInput = Omit<AuditEntry, "timestamp"> & { timestamp?: string };

/** The surface the MCP layer depends on, so tests can inject a fake sink. */
export interface AuditSink {
  write(entry: AuditEntryInput): void;
}

export interface AuditLogOptions {
  /** Directory that holds one `YYYY-MM-DD.jsonl` file per UTC day. */
  dir: string;
  /** Days of entries to keep; defaults to the design's seven. */
  retentionDays?: number;
  /** Injectable clock for tests. */
  now?: () => Date;
  /** Receives best-effort write/prune failures; defaults to stderr. */
  onError?: (error: unknown) => void;
}

const AUDIT_FILE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})\.jsonl$/;

/**
 * Appends metadata-only JSONL audit entries under `<dir>/YYYY-MM-DD.jsonl`.
 *
 * Auditing is best-effort: a directory, disk, or permission failure is
 * reported through `onError` (stderr by default, never stdout, because stdio
 * owns stdout) and never fails the tool call. Files older than the retention
 * window are pruned once when the log is constructed.
 */
export class AuditLog implements AuditSink {
  private readonly dir: string;
  private readonly retentionDays: number;
  private readonly now: () => Date;
  private readonly onError: (error: unknown) => void;
  private errorReported = false;

  constructor(options: AuditLogOptions) {
    this.dir = options.dir;
    this.retentionDays = normalizeRetention(options.retentionDays);
    this.now = options.now ?? (() => new Date());
    this.onError = options.onError ?? ((error: unknown) => {
      console.error("kennect-computer audit log failed:", error);
    });

    this.initialize();
  }

  write(entry: AuditEntryInput): void {
    try {
      const { timestamp, when } = this.resolveTimestamp(entry.timestamp);
      const record: AuditEntry = {
        timestamp,
        sessionId: entry.sessionId,
        tool: entry.tool,
        result: entry.result,
        durationMs: entry.durationMs,
      };

      // Copy only the known target identifiers; anything else in the input is
      // dropped so titles, messages, or content can never reach disk (R38).
      if (entry.windowId !== undefined) {
        record.windowId = entry.windowId;
      }
      if (entry.process !== undefined) {
        record.process = entry.process;
      }
      if (entry.nodeId !== undefined) {
        record.nodeId = entry.nodeId;
      }
      if (entry.x !== undefined) {
        record.x = entry.x;
      }
      if (entry.y !== undefined) {
        record.y = entry.y;
      }
      if (entry.textLength !== undefined) {
        record.textLength = entry.textLength;
      }
      if (entry.textSha256 !== undefined) {
        record.textSha256 = entry.textSha256;
      }

      mkdirSync(this.dir, { recursive: true });
      appendFileSync(
        path.join(this.dir, dayFileName(when)),
        `${JSON.stringify(record)}\n`,
        "utf8",
      );
      this.errorReported = false;
    } catch (error) {
      this.reportError(error);
    }
  }

  private initialize(): void {
    try {
      mkdirSync(this.dir, { recursive: true });
    } catch (error) {
      this.reportError(error);
      return;
    }

    this.prune();
  }

  /** Deletes day files outside the retention window; unrelated files stay. */
  private prune(): void {
    const cutoff = startOfUtcDay(this.now());
    cutoff.setUTCDate(cutoff.getUTCDate() - Math.max(0, this.retentionDays - 1));

    let names: string[];
    try {
      names = readdirSync(this.dir);
    } catch (error) {
      this.reportError(error);
      return;
    }

    for (const name of names) {
      const match = AUDIT_FILE_PATTERN.exec(name);
      if (match === null) {
        continue;
      }

      const day = new Date(
        Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])),
      );
      if (day.getTime() >= cutoff.getTime()) {
        continue;
      }

      try {
        unlinkSync(path.join(this.dir, name));
      } catch (error) {
        this.reportError(error);
      }
    }
  }

  private resolveTimestamp(provided: string | undefined): { timestamp: string; when: Date } {
    if (provided !== undefined) {
      const parsed = new Date(provided);
      if (!Number.isNaN(parsed.getTime())) {
        return { timestamp: provided, when: parsed };
      }
    }

    const when = this.now();
    return { timestamp: when.toISOString(), when };
  }

  /** Reports the first failure in a run of failures to avoid per-call spam. */
  private reportError(error: unknown): void {
    if (this.errorReported) {
      return;
    }

    this.errorReported = true;
    this.onError(error);
  }
}

function normalizeRetention(retentionDays: number | undefined): number {
  if (retentionDays === undefined || !Number.isFinite(retentionDays)) {
    return AUDIT_RETENTION_DAYS;
  }

  return Math.max(0, Math.floor(retentionDays));
}

function startOfUtcDay(date: Date): Date {
  return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
}

function dayFileName(date: Date): string {
  return `${date.toISOString().slice(0, 10)}.jsonl`;
}
