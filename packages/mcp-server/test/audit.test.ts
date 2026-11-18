import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AuditLog, type AuditEntryInput } from "../src/audit/log.js";
import { resolveAuditRetentionDays } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer, type McpServerOptions } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { readStatusFixture } from "./support/fixture.js";
import { notepadSnapshot, notepadWindow } from "./support/snapshot-fixture.js";

const FIXED_NOW = new Date("2026-11-10T12:00:00Z");

const temporaryDirectories: string[] = [];

function temporaryDirectory(): string {
  const dir = mkdtempSync(path.join(tmpdir(), "kennect-audit-"));
  temporaryDirectories.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of temporaryDirectories.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

function readAuditLines(dir: string, day = "2026-11-10"): Array<Record<string, unknown>> {
  return readFileSync(path.join(dir, `${day}.jsonl`), "utf8")
    .trim()
    .split("\n")
    .map((line) => JSON.parse(line) as Record<string, unknown>);
}

function fixedNow(): Date {
  return FIXED_NOW;
}

describe("AuditLog", () => {
  it("writes one JSONL line with no field content", () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    log.write({
      sessionId: "s1",
      tool: "computer_snapshot",
      windowId: "w_1",
      process: "Feishu.exe",
      result: "ok",
      durationMs: 12,
    });

    const line = JSON.parse(
      readFileSync(path.join(dir, "2026-11-10.jsonl"), "utf8").trim(),
    ) as Record<string, unknown>;

    expect(line).toEqual({
      timestamp: "2026-11-10T12:00:00.000Z",
      sessionId: "s1",
      tool: "computer_snapshot",
      windowId: "w_1",
      process: "Feishu.exe",
      result: "ok",
      durationMs: 12,
    });
    expect(line.tool).toBe("computer_snapshot");
    expect(line).not.toHaveProperty("text");
    expect(line).not.toHaveProperty("content");
    expect(line).not.toHaveProperty("message");
    expect(line).not.toHaveProperty("title");
  });

  it("deletes files older than the retention window", () => {
    const dir = temporaryDirectory();
    const old = path.join(dir, "2026-11-01.jsonl");
    writeFileSync(old, "{}\n");

    new AuditLog({ dir, retentionDays: 7, now: () => new Date("2026-11-10T00:00:00Z") });

    expect(existsSync(old)).toBe(false);
  });

  it("keeps files inside the retention window and leaves unrelated files alone", () => {
    const dir = temporaryDirectory();
    const kept = path.join(dir, "2026-11-09.jsonl");
    const unrelated = path.join(dir, "notes.txt");
    writeFileSync(kept, "{}\n");
    writeFileSync(unrelated, "keep me\n");

    new AuditLog({ dir, retentionDays: 7, now: fixedNow });

    expect(existsSync(kept)).toBe(true);
    expect(existsSync(unrelated)).toBe(true);
  });

  it("defaults to seven days of retention", () => {
    const dir = temporaryDirectory();
    const expired = path.join(dir, "2026-11-02.jsonl");
    const kept = path.join(dir, "2026-11-04.jsonl");
    writeFileSync(expired, "{}\n");
    writeFileSync(kept, "{}\n");

    new AuditLog({ dir, now: fixedNow });

    expect(existsSync(expired)).toBe(false);
    expect(existsSync(kept)).toBe(true);
  });

  it("deletes exactly the day outside the retention window", () => {
    // Seven days from 2026-11-10 includes 2026-11-04 and excludes 2026-11-03.
    const dir = temporaryDirectory();
    const outside = path.join(dir, "2026-11-03.jsonl");
    const boundary = path.join(dir, "2026-11-04.jsonl");
    writeFileSync(outside, "{}\n");
    writeFileSync(boundary, "{}\n");

    new AuditLog({
      dir,
      retentionDays: 7,
      now: () => new Date("2026-11-10T23:59:59Z"),
    });

    expect(existsSync(outside)).toBe(false);
    expect(existsSync(boundary)).toBe(true);
  });

  it("uses the configured retention to prune entries", () => {
    const dir = temporaryDirectory();
    const configPath = path.join(dir, "config.json");
    writeFileSync(
      configPath,
      JSON.stringify({ privacy: { auditRetentionDays: 2 } }),
      "utf8",
    );

    const auditDir = path.join(dir, "audit");
    mkdirSync(auditDir, { recursive: true });
    const expired = path.join(auditDir, "2026-11-08.jsonl");
    const kept = path.join(auditDir, "2026-11-09.jsonl");
    writeFileSync(expired, "{}\n");
    writeFileSync(kept, "{}\n");

    new AuditLog({
      dir: auditDir,
      retentionDays: resolveAuditRetentionDays({ configPath, userConfigPath: null }),
      now: fixedNow,
    });

    expect(existsSync(expired)).toBe(false);
    expect(existsSync(kept)).toBe(true);
  });

  it("reports a run of write failures once", () => {
    const parent = temporaryDirectory();
    const unwritable = path.join(parent, "occupied");
    writeFileSync(unwritable, "not a directory\n");
    const onError = vi.fn();

    const log = new AuditLog({ dir: unwritable, now: fixedNow, onError });

    log.write({ sessionId: "s1", tool: "computer_status", result: "ok", durationMs: 1 });
    log.write({ sessionId: "s1", tool: "computer_windows", result: "ok", durationMs: 2 });
    log.write({ sessionId: "s1", tool: "computer_stop", result: "ok", durationMs: 3 });

    // One report for the whole failing run, not one per call.
    expect(onError).toHaveBeenCalledTimes(1);
  });

  it("writes one file per UTC day and appends entries", () => {
    const dir = temporaryDirectory();
    let current = new Date("2026-11-10T23:59:59Z");
    const log = new AuditLog({ dir, retentionDays: 7, now: () => current });

    log.write({ sessionId: "s1", tool: "computer_status", result: "ok", durationMs: 1 });
    current = new Date("2026-11-11T00:00:01Z");
    log.write({ sessionId: "s1", tool: "computer_windows", result: "ok", durationMs: 2 });
    log.write({ sessionId: "s1", tool: "computer_stop", result: "ok", durationMs: 3 });

    expect(readAuditLines(dir, "2026-11-10")).toHaveLength(1);
    expect(readAuditLines(dir, "2026-11-11").map((line) => line.tool)).toEqual([
      "computer_windows",
      "computer_stop",
    ]);
  });

  it("drops fields outside the audit schema", () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    log.write({
      sessionId: "s1",
      tool: "computer_focus",
      windowId: "w_1",
      process: "notepad",
      nodeId: "n3:2",
      result: "ok",
      durationMs: 5,
      title: "Untitled - Notepad",
      text: "typed message",
      content: "screen content",
      message: "failure detail",
    } as unknown as AuditEntryInput);

    const [line] = readAuditLines(dir);

    expect(line).toEqual({
      timestamp: "2026-11-10T12:00:00.000Z",
      sessionId: "s1",
      tool: "computer_focus",
      windowId: "w_1",
      process: "notepad",
      nodeId: "n3:2",
      result: "ok",
      durationMs: 5,
    });
  });

  it("writes type text fingerprints instead of the text", () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    log.write({
      sessionId: "s1",
      tool: "computer_type",
      nodeId: "n3:2",
      textLength: 13,
      textSha256: "a".repeat(64),
      result: "ok",
      durationMs: 4,
      text: "typed message",
      title: "Untitled - Notepad",
      message: "failure detail",
    } as unknown as AuditEntryInput);

    const [line] = readAuditLines(dir);

    expect(line).toEqual({
      timestamp: "2026-11-10T12:00:00.000Z",
      sessionId: "s1",
      tool: "computer_type",
      nodeId: "n3:2",
      textLength: 13,
      textSha256: "a".repeat(64),
      result: "ok",
      durationMs: 4,
    });
    expect(JSON.stringify(line)).not.toContain("typed message");
  });

  it("never throws when the audit directory is unwritable", () => {
    const parent = temporaryDirectory();
    const filePath = path.join(parent, "not-a-directory");
    writeFileSync(filePath, "occupied\n");
    const onError = vi.fn();

    const log = new AuditLog({ dir: filePath, now: fixedNow, onError });

    expect(() => {
      log.write({ sessionId: "s1", tool: "computer_status", result: "ok", durationMs: 1 });
    }).not.toThrow();
    expect(onError).toHaveBeenCalled();
  });
});

function fakeHost(): HostRequester {
  return {
    request: async (method: string) => {
      switch (method) {
        case "status":
          return readStatusFixture();
        case "windows.list":
          return { windows: [notepadWindow] };
        case "window.select":
          return { window: notepadWindow };
        case "snapshot":
          return notepadSnapshot;
        case "action":
          return { window: notepadWindow };
        case "stop":
          return { ok: true };
        default:
          throw new Error(`Unexpected host method in the fake: ${method}`);
      }
    },
  };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("MCP audit wiring", () => {
  const servers: Server[] = [];
  const clients: Client[] = [];

  afterEach(async () => {
    for (const client of clients.splice(0)) {
      await client.close();
    }
    for (const server of servers.splice(0)) {
      await server.close();
    }
  });

  async function connect(provider: HostProvider, options: McpServerOptions): Promise<Client> {
    const server = createMcpServer(provider, options);
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport);

    const client = new Client({ name: "audit-test-client", version: "1.0.0" });
    await client.connect(clientTransport);

    servers.push(server);
    clients.push(client);
    return client;
  }

  it("audits every M1 tool through the in-memory MCP transport without content", async () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    const client = await connect(providerFor(fakeHost()), {
      audit: log,
      sessionId: "s_e2e",
    });

    const { tools } = await client.listTools();
    expect(tools.map((tool) => tool.name)).toEqual([
      "computer_status",
      "computer_windows",
      "computer_select",
      "computer_focus",
      "computer_snapshot",
      "computer_stop",
      "computer_click",
      "computer_type",
      "computer_key",
      "computer_scroll",
      "computer_find",
      "computer_wait",
      "computer_read_text",
      "computer_batch",
    ]);

    const calls: Array<{ name: string; arguments: Record<string, unknown> }> = [
      { name: "computer_status", arguments: {} },
      { name: "computer_windows", arguments: {} },
      {
        name: "computer_select",
        arguments: { windowId: "w_1", title: "Untitled - Notepad" },
      },
      { name: "computer_snapshot", arguments: { windowId: "w_1", image: true } },
      { name: "computer_focus", arguments: { windowId: "w_1" } },
      { name: "computer_stop", arguments: {} },
    ];

    for (const call of calls) {
      const result = await client.callTool({ name: call.name, arguments: call.arguments });
      expect(result.isError).toBe(false);
    }

    const lines = readAuditLines(dir);
    expect(lines).toHaveLength(6);
    expect(lines.map((line) => line.tool)).toEqual(calls.map((call) => call.name));

    for (const line of lines) {
      expect(line.sessionId).toBe("s_e2e");
      expect(line.timestamp).toBe("2026-11-10T12:00:00.000Z");
      expect(line.result).toBe("ok");
      expect(typeof line.durationMs).toBe("number");
      expect(line.durationMs as number).toBeGreaterThanOrEqual(0);
      expect(line).not.toHaveProperty("text");
      expect(line).not.toHaveProperty("content");
      expect(line).not.toHaveProperty("message");
      expect(line).not.toHaveProperty("title");
    }

    expect(lines[2]?.windowId).toBe("w_1");
    expect(lines[3]?.windowId).toBe("w_1");
    expect(lines[4]?.windowId).toBe("w_1");
    expect(lines[0]).not.toHaveProperty("windowId");
    expect(lines[1]).not.toHaveProperty("windowId");
    expect(lines[5]).not.toHaveProperty("windowId");
    expect(JSON.stringify(lines)).not.toContain("Untitled - Notepad");
  });

  it("records the acted-on window and coordinates for an action tool", async () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    const request = vi.fn().mockResolvedValue({
      window: notepadWindow,
      action: "click",
      method: "coordinates",
      needsObservation: true,
    });
    const client = await connect(providerFor({ request }), {
      audit: log,
      sessionId: "s_action",
    });

    const result = await client.callTool({
      name: "computer_click",
      arguments: { x: 10, y: 20, space: "screen" },
    });

    expect(result.isError).toBe(false);
    const [line] = readAuditLines(dir);
    expect(line).toMatchObject({
      tool: "computer_click",
      windowId: "w_1",
      process: "notepad",
      x: 10,
      y: 20,
      result: "ok",
    });
  });

  it("records the acted-on window for a batch from its step results", async () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    const request = vi.fn().mockResolvedValue({
      steps: [
        {
          index: 0,
          kind: "click",
          ok: true,
          data: {
            window: notepadWindow,
            action: "click",
            method: "pattern",
            needsObservation: true,
          },
        },
      ],
    });
    const client = await connect(providerFor({ request }), {
      audit: log,
      sessionId: "s_batch",
    });

    const result = await client.callTool({
      name: "computer_batch",
      arguments: { steps: [{ kind: "click", nodeId: "n3:2" }] },
    });

    expect(result.isError).toBe(false);
    const [line] = readAuditLines(dir);
    expect(line).toMatchObject({
      tool: "computer_batch",
      windowId: "w_1",
      process: "notepad",
      result: "ok",
    });
  });

  it("records the error code for a failed call without the message", async () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: fixedNow });
    const client = await connect(
      {
        ensureHost: async () => {
          throw new HostError("HOST_UNAVAILABLE", "Sensitive host detail 123");
        },
      },
      { audit: log, sessionId: "s_fail" },
    );

    const result = await client.callTool({ name: "computer_status", arguments: {} });

    expect(result.isError).toBe(true);
    const [line] = readAuditLines(dir);
    expect(line?.tool).toBe("computer_status");
    expect(line?.result).toBe("HOST_UNAVAILABLE");
    expect(JSON.stringify(line)).not.toContain("Sensitive host detail 123");
  });

  it("keeps the tool result when the audit sink throws", async () => {
    const write = vi.fn(() => {
      throw new Error("audit disk full");
    });
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});

    try {
      const client = await connect(providerFor(fakeHost()), {
        audit: { write },
        sessionId: "s_best_effort",
      });

      const result = await client.callTool({ name: "computer_status", arguments: {} });

      expect(result.isError).toBe(false);
      expect(write).toHaveBeenCalledTimes(1);
      expect(consoleError).toHaveBeenCalled();
    } finally {
      consoleError.mockRestore();
    }
  });

  it("does not audit calls rejected by argument validation", async () => {
    const write = vi.fn();
    const client = await connect(providerFor(fakeHost()), {
      audit: { write },
      sessionId: "s_invalid",
    });

    await expect(
      client.callTool({ name: "computer_status", arguments: { unexpected: true } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });

    expect(write).not.toHaveBeenCalled();
  });
});
