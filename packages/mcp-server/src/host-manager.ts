import { spawn as spawnProcess } from "node:child_process";
import { readFileSync, unlinkSync } from "node:fs";
import path from "node:path";

import { PROTOCOL_VERSION } from "@kennect/shared";
import { z } from "zod";

import {
  defaultSessionDir,
  HOST_PATH_ENV,
  SESSION_FILE_NAME,
  SPAWN_WAIT_MS,
  TOKEN_FILE_NAME,
} from "./config.js";
import { errorCodeOf, HostError, toHostError } from "./errors.js";
import { HostClient } from "./host-client.js";
import type { HostRequester } from "./tools/status.js";

const POLL_INTERVAL_MS = 50;

const SessionInfoSchema = z.object({
  pipeName: z.string().min(1),
  pid: z.number().int().positive(),
  protocolVersion: z.number().int(),
  startedAt: z.string(),
});

export type SessionInfo = z.infer<typeof SessionInfoSchema>;

/** The subset of HostClient that tools depend on. */
export interface HostConnection extends HostRequester {
  close(): void;
  isConnected?(): boolean;
}

export interface HostManagerOptions {
  /** Discovery directory; defaults to `%LOCALAPPDATA%\KennectComputer`. */
  sessionDir?: string;
  /** Host executable; defaults to `KENNECT_HOST_PATH`. */
  hostPath?: string;
  /** Override the host launcher (tests use a no-op). */
  spawn?: (hostPath: string) => Promise<void>;
  /** Override discovery (tests inject a fixed session or null). */
  readSession?: () => SessionInfo | null;
  /** Override connection creation (tests inject fake clients). */
  connect?: (session: SessionInfo | null, sessionDir: string) => Promise<HostConnection>;
  /** Override the PID liveness probe. */
  isProcessAlive?: (pid: number) => boolean;
  /** How long to wait for the session files after spawning. */
  spawnWaitMs?: number;
  /** Override the poll delay (tests shorten it). */
  sleep?: (milliseconds: number) => Promise<void>;
}

/**
 * Owns the single host connection: reads discovery metadata, prunes a stale
 * session, connects, and respawns the host at most once per `ensureHost` call.
 */
export class HostManager {
  private readonly sessionDir: string;
  private readonly hostPath: string | undefined;
  private readonly options: HostManagerOptions;
  private readonly spawnWaitMs: number;
  private readonly sleep: (milliseconds: number) => Promise<void>;
  private readonly readSession: () => SessionInfo | null;
  private readonly connect: (
    session: SessionInfo | null,
    sessionDir: string,
  ) => Promise<HostConnection>;
  private readonly isProcessAlive: (pid: number) => boolean;

  private client: HostConnection | null = null;
  private pendingEnsure: Promise<HostConnection> | null = null;

  constructor(options: HostManagerOptions = {}) {
    this.options = options;
    this.sessionDir = options.sessionDir ?? defaultSessionDir();
    this.hostPath = options.hostPath ?? process.env[HOST_PATH_ENV];
    this.spawnWaitMs = options.spawnWaitMs ?? SPAWN_WAIT_MS;
    this.sleep = options.sleep
      ?? ((milliseconds: number) => new Promise<void>((resolve) => {
        setTimeout(resolve, milliseconds);
      }));
    this.readSession = options.readSession
      ?? (() => readSessionFile(this.sessionDir));
    this.connect = options.connect ?? HostManager.connectToSession;
    this.isProcessAlive = options.isProcessAlive ?? isPidAlive;
  }

  /** Returns a live connection, spawning the host at most once when needed. */
  async ensureHost(): Promise<HostConnection> {
    if (this.client !== null && this.isUsable(this.client)) {
      return this.client;
    }

    this.client = null;

    if (this.pendingEnsure === null) {
      const operation = this.provision();
      this.pendingEnsure = operation;

      const clear = (): void => {
        if (this.pendingEnsure === operation) {
          this.pendingEnsure = null;
        }
      };

      // Observe rejection here so the shared promise never becomes unhandled.
      void operation.then(clear, clear);
    }

    return this.pendingEnsure;
  }

  close(): void {
    this.client?.close();
    this.client = null;
  }

  private async provision(): Promise<HostConnection> {
    let session = this.readSession();

    // R24: a dead PID means the host crashed without cleaning up session.json.
    if (session !== null && !this.isProcessAlive(session.pid)) {
      this.pruneSession();
      session = null;
    }

    try {
      const client = await this.connect(session, this.sessionDir);
      this.client = client;
      return client;
    } catch (error) {
      if (!this.shouldSpawn(error)) {
        throw toHostError(error, "The Kennect host is unavailable.");
      }
    }

    await this.spawnOnce();
    await this.waitForSession();

    try {
      const client = await this.connect(this.readSession(), this.sessionDir);
      this.client = client;
      return client;
    } catch (error) {
      throw toHostError(
        error,
        "The Kennect host did not become available after it was started. Start it manually and retry.",
      );
    }
  }

  /** Spawn only for absent/refused connections; auth and timeout failures are real. */
  private shouldSpawn(error: unknown): boolean {
    const code = errorCodeOf(error);
    return code === undefined || code === "HOST_UNAVAILABLE";
  }

  private async spawnOnce(): Promise<void> {
    if (this.options.spawn !== undefined) {
      try {
        await this.options.spawn(this.hostPath ?? "");
        return;
      } catch (error) {
        throw toHostError(error, "Failed to start the Kennect host.");
      }
    }

    if (this.hostPath === undefined || this.hostPath.length === 0) {
      throw new HostError(
        "HOST_UNAVAILABLE",
        `The Kennect host is not running and ${HOST_PATH_ENV} is not set. Set ${HOST_PATH_ENV} to the host executable or start the host manually.`,
      );
    }

    await spawnHost(this.hostPath);
  }

  private async waitForSession(): Promise<void> {
    const deadline = Date.now() + this.spawnWaitMs;

    while (Date.now() < deadline) {
      const session = this.readSession();
      if (session !== null && this.isProcessAlive(session.pid)) {
        return;
      }

      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        break;
      }

      await this.sleep(Math.min(POLL_INTERVAL_MS, remaining));
    }
  }

  private pruneSession(): void {
    try {
      unlinkSync(path.join(this.sessionDir, SESSION_FILE_NAME));
    } catch {
      // Best effort: the new host will overwrite it, and a refused write is not fatal.
    }
  }

  private isUsable(client: HostConnection): boolean {
    return typeof client.isConnected === "function" ? client.isConnected() : true;
  }

  private static async connectToSession(
    session: SessionInfo | null,
    sessionDir: string,
  ): Promise<HostConnection> {
    if (session === null) {
      throw new HostError(
        "HOST_UNAVAILABLE",
        "No Kennect host session was found. Start the host and retry.",
      );
    }

    if (session.protocolVersion !== PROTOCOL_VERSION) {
      throw new HostError(
        "HOST_UNAVAILABLE",
        `The Kennect host speaks protocol ${session.protocolVersion}, but this server requires ${PROTOCOL_VERSION}.`,
      );
    }

    const client = new HostClient(
      session.pipeName,
      path.join(sessionDir, TOKEN_FILE_NAME),
    );
    await client.connect();
    return client;
  }
}

function readSessionFile(sessionDir: string): SessionInfo | null {
  try {
    const raw = readFileSync(path.join(sessionDir, SESSION_FILE_NAME), "utf8");
    const parsed = SessionInfoSchema.safeParse(JSON.parse(raw));
    return parsed.success ? parsed.data : null;
  } catch {
    // Missing or malformed discovery metadata is treated as "not running".
    return null;
  }
}

function isPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM means the process exists but belongs to another user.
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

function spawnHost(hostPath: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    let child: ReturnType<typeof spawnProcess>;
    try {
      child = spawnProcess(hostPath, [], {
        detached: true,
        stdio: "ignore",
        windowsHide: false,
      });
    } catch (error) {
      reject(
        new HostError(
          "HOST_UNAVAILABLE",
          `Failed to start the Kennect host at ${hostPath}: ${describe(error)}`,
          { cause: error },
        ),
      );
      return;
    }

    let settled = false;

    child.once("error", (error: Error) => {
      if (settled) {
        return;
      }
      settled = true;
      reject(
        new HostError(
          "HOST_UNAVAILABLE",
          `Failed to start the Kennect host at ${hostPath}: ${error.message}`,
          { cause: error },
        ),
      );
    });

    child.once("spawn", () => {
      if (settled) {
        return;
      }
      settled = true;
      child.unref();
      resolve();
    });
  });
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
