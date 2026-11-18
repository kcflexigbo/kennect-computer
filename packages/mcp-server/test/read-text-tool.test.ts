import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { ReadTextResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AuditLog } from "../src/audit/log.js";
import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { readTextTool } from "../src/tools/read-text.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadWindow } from "./support/snapshot-fixture.js";

const FIXED_NOW = new Date("2026-11-10T12:00:00Z");
const READ_TEXT = "Kennect 汉字 123";

const readTextResult: ReadTextResult = {
  window: notepadWindow,
  nodeId: "n3:2",
  text: READ_TEXT,
  length: READ_TEXT.length,
  truncated: false,
};

const temporaryDirectories: string[] = [];

function temporaryDirectory(): string {
  const dir = mkdtempSync(path.join(tmpdir(), "kennect-read-text-audit-"));
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

describe("readTextTool", () => {
  it("sends the read_text request and returns the validated result", async () => {
    const request = vi.fn().mockResolvedValue(readTextResult);

    const result = await readTextTool(requester(request), { nodeId: "n3:2" });

    expect(request).toHaveBeenCalledWith(
      "read_text",
      { nodeId: "n3:2" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: readTextResult });
  });

  it("forwards the maximum length", async () => {
    const request = vi.fn().mockResolvedValue(readTextResult);

    await readTextTool(requester(request), { nodeId: "n3:2", maxLength: 40 });

    expect(request).toHaveBeenCalledWith(
      "read_text",
      { nodeId: "n3:2", maxLength: 40 },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("maps host failures to the shared envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("INVALID_REQUEST", "The control does not expose text."),
    );

    const result = await readTextTool(requester(request), { nodeId: "n3:2" });

    expect(result).toEqual({
      ok: false,
      code: "INVALID_REQUEST",
      message: "The control does not expose text.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the read text result schema", async () => {
    const request = vi.fn().mockResolvedValue({
      window: notepadWindow,
      nodeId: "n3:2",
      length: 4,
    });

    const result = await readTextTool(requester(request), { nodeId: "n3:2" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("read text");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await readTextTool(requester(request), { nodeId: "n3:2" });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_read_text MCP tool", () => {
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

  it("returns the text in the envelope as structuredContent plus text", async () => {
    const request = vi.fn().mockResolvedValue(readTextResult);
    const client = await connect(providerFor({ request }));
    const expected = { ok: true, data: readTextResult };

    const result = await client.callTool({
      name: "computer_read_text",
      arguments: { nodeId: "n3:2", maxLength: 40 },
    });

    expect(request).toHaveBeenCalledWith(
      "read_text",
      { nodeId: "n3:2", maxLength: 40 },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
    expect(JSON.stringify(result.content)).toContain(READ_TEXT);
  });

  it("audits the target and outcome but never the text", async () => {
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: () => FIXED_NOW });
    const request = vi.fn().mockResolvedValue(readTextResult);
    const client = await connect(providerFor({ request }), {
      audit: log,
      sessionId: "s_read_text",
    });

    const result = await client.callTool({
      name: "computer_read_text",
      arguments: { nodeId: "n3:2", maxLength: 40 },
    });

    expect(result.isError).toBe(false);

    const line = JSON.parse(
      readFileSync(path.join(dir, "2026-11-10.jsonl"), "utf8").trim(),
    ) as Record<string, unknown>;

    expect(line).toMatchObject({
      timestamp: "2026-11-10T12:00:00.000Z",
      sessionId: "s_read_text",
      tool: "computer_read_text",
      nodeId: "n3:2",
      result: "ok",
    });
    expect(typeof line.durationMs).toBe("number");
    expect(line).not.toHaveProperty("text");
    expect(line).not.toHaveProperty("content");
    expect(line).not.toHaveProperty("message");
    expect(line).not.toHaveProperty("title");
    expect(line).not.toHaveProperty("maxLength");
    expect(JSON.stringify(line)).not.toContain("Kennect");
    expect(JSON.stringify(line)).not.toContain("汉字");
  });

  it("rejects a missing nodeId as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_read_text", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects an empty nodeId as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_read_text",
        arguments: { nodeId: "" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_read_text",
        arguments: { nodeId: "n3:2", text: "read this" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
