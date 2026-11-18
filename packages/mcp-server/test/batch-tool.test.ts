import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type {
  BatchRequest,
  BatchResult,
  BatchStepResult,
  ReadTextResult,
} from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AuditLog } from "../src/audit/log.js";
import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { batchTool } from "../src/tools/batch.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import {
  notepadSnapshot,
  notepadWindow,
} from "./support/snapshot-fixture.js";

const FIXED_NOW = new Date("2026-11-10T12:00:00Z");

const keyStep: BatchStepResult = {
  index: 0,
  kind: "key",
  ok: true,
  data: {
    window: notepadWindow,
    action: "key",
    method: "key",
    needsObservation: true,
  },
};

const readTextStepData: ReadTextResult = {
  window: notepadWindow,
  nodeId: "n3:2",
  text: "Batch text",
  length: "Batch text".length,
  truncated: false,
};

const readTextStep: BatchStepResult = {
  index: 1,
  kind: "readText",
  ok: true,
  data: readTextStepData,
};

const successBatch: BatchResult = {
  steps: [keyStep, readTextStep],
  snapshotBefore: notepadSnapshot,
  snapshotAfter: { ...notepadSnapshot, generation: 4 },
};

const failedBatch: BatchResult = {
  steps: [
    keyStep,
    {
      index: 1,
      kind: "click",
      ok: false,
      code: "STALE_SNAPSHOT",
      message: "The node is from an older snapshot.",
    },
  ],
  failedIndex: 1,
  snapshotAfter: notepadSnapshot,
};

const batchArgs: BatchRequest = {
  steps: [
    { kind: "key", keys: "Enter" },
    { kind: "readText", nodeId: "n3:2" },
  ],
  snapshotBefore: true,
  snapshotAfter: true,
};

const temporaryDirectories: string[] = [];

function temporaryDirectory(): string {
  const dir = mkdtempSync(path.join(tmpdir(), "kennect-batch-audit-"));
  temporaryDirectories.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of temporaryDirectories.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("batchTool", () => {
  it("sends the ordered batch and returns the validated result", async () => {
    const request = vi.fn().mockResolvedValue(successBatch);

    const result = await batchTool(requester(request), batchArgs);

    expect(request).toHaveBeenCalledWith(
      "action.batch",
      batchArgs,
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: successBatch });
  });

  it("returns a failed step as an ok:false envelope with the partial step results", async () => {
    const request = vi.fn().mockResolvedValue(failedBatch);

    const result = await batchTool(requester(request), batchArgs);

    expect(result).toEqual({
      ok: false,
      code: "STALE_SNAPSHOT",
      message: "The node is from an older snapshot.",
      retryable: false,
      partial: {
        completed: 1,
        failedIndex: 1,
        results: failedBatch.steps,
      },
    });
    expect(result.data).toBeUndefined();
  });

  it("reports a step that never ran after a stop as ACTION_TIMEOUT", async () => {
    const stopped: BatchResult = { steps: [keyStep], failedIndex: 1 };
    const request = vi.fn().mockResolvedValue(stopped);

    const result = await batchTool(requester(request), batchArgs);

    expect(result).toEqual({
      ok: false,
      code: "ACTION_TIMEOUT",
      message: "The operation was stopped.",
      retryable: false,
      partial: {
        completed: 1,
        failedIndex: 1,
        results: stopped.steps,
      },
    });
  });

  it("maps host failures to the shared envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("LOCKED_BY_OTHER_SESSION", "Another session holds control."),
    );

    const result = await batchTool(requester(request), batchArgs);

    expect(result).toEqual({
      ok: false,
      code: "LOCKED_BY_OTHER_SESSION",
      message: "Another session holds control.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the batch result schema", async () => {
    const request = vi.fn().mockResolvedValue({ steps: "not-an-array" });

    const result = await batchTool(requester(request), batchArgs);

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("batch");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await batchTool(requester(request), batchArgs);

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_batch MCP tool", () => {
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

  async function connect(
    provider: HostProvider,
    options: { audit?: AuditLog; sessionId?: string } = {},
  ): Promise<Client> {
    const server = createMcpServer(provider, options);
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport);

    const client = new Client({ name: "test-client", version: "1.0.0" });
    await client.connect(clientTransport);

    servers.push(server);
    clients.push(client);
    return client;
  }

  it("returns a successful batch as structuredContent plus one compact JSON text block", async () => {
    const request = vi.fn().mockResolvedValue(successBatch);
    const client = await connect(providerFor({ request }));
    const expected = { ok: true, data: successBatch };

    const result = await client.callTool({
      name: "computer_batch",
      arguments: batchArgs,
    });

    expect(request).toHaveBeenCalledWith(
      "action.batch",
      batchArgs,
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
    expect(JSON.stringify(result.content)).not.toContain("\"type\":\"image\"");
  });

  it("returns a failed step as an isError envelope with the failed index and step details", async () => {
    const request = vi.fn().mockResolvedValue(failedBatch);
    const client = await connect(providerFor({ request }));
    const expected = {
      ok: false,
      code: "STALE_SNAPSHOT",
      message: "The node is from an older snapshot.",
      retryable: false,
      partial: {
        completed: 1,
        failedIndex: 1,
        results: failedBatch.steps,
      },
    };

    const result = await client.callTool({
      name: "computer_batch",
      arguments: batchArgs,
    });

    const text = JSON.stringify(expected);

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([{ type: "text", text }]);
    expect(text).toContain("\"failedIndex\":1");
    expect(text).toContain("\"ok\":false");
  });

  it("audits the batch outcome but never step text or content", async () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: () => FIXED_NOW });
    const secret = "Batch audit secret";
    const secretResult: BatchResult = {
      steps: [
        {
          ...readTextStep,
          data: { ...readTextStepData, text: secret, length: secret.length },
        },
      ],
    };
    const request = vi.fn().mockResolvedValue(secretResult);
    const client = await connect(providerFor({ request }), {
      audit: log,
      sessionId: "s_batch",
    });

    const result = await client.callTool({
      name: "computer_batch",
      arguments: { steps: [{ kind: "readText", nodeId: "n3:2" }] },
    });

    expect(result.isError).toBe(false);

    const line = JSON.parse(
      readFileSync(path.join(dir, "2026-11-10.jsonl"), "utf8").trim(),
    ) as Record<string, unknown>;

    expect(line).toMatchObject({
      timestamp: "2026-11-10T12:00:00.000Z",
      sessionId: "s_batch",
      tool: "computer_batch",
      result: "ok",
    });
    expect(typeof line.durationMs).toBe("number");
    expect(line).not.toHaveProperty("text");
    expect(line).not.toHaveProperty("content");
    expect(line).not.toHaveProperty("message");
    expect(line).not.toHaveProperty("title");
    expect(JSON.stringify(line)).not.toContain(secret);
  });

  it("rejects an empty steps array as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_batch", arguments: { steps: [] } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects more than 50 steps as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));
    const steps = Array.from({ length: 51 }, () => ({ kind: "key", keys: "Tab" }));

    await expect(
      client.callTool({ name: "computer_batch", arguments: { steps } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a step with an unknown kind as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_batch",
        arguments: { steps: [{ kind: "drag", nodeId: "n3:2" }] },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a step with an invalid request as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_batch",
        arguments: { steps: [{ kind: "click", x: 1, y: 2, nodeId: "n3:2" }] },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown top-level arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_batch",
        arguments: { steps: [{ kind: "key", keys: "Tab" }], timeoutMs: 10 },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
