import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { ActionResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { AuditLog } from "../src/audit/log.js";
import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer, type McpServerOptions } from "../src/index.js";
import { typeTool } from "../src/tools/type.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadWindow } from "./support/snapshot-fixture.js";

const FIXED_NOW = new Date("2026-11-10T12:00:00Z");

const typeResult: ActionResult = {
  window: notepadWindow,
  action: "type",
  method: "paste",
  needsObservation: true,
};

const temporaryDirectories: string[] = [];

function temporaryDirectory(): string {
  const dir = mkdtempSync(path.join(tmpdir(), "kennect-type-audit-"));
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

describe("typeTool", () => {
  it("sends the type action without a method so the host default applies", async () => {
    const request = vi.fn().mockResolvedValue(typeResult);

    const result = await typeTool(requester(request), { text: "Hello" });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "type", text: "Hello" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: typeResult });
  });

  it("forwards an explicit method, node ID, and expect predicates", async () => {
    const request = vi.fn().mockResolvedValue(typeResult);
    const args = {
      text: "Hello",
      method: "keys" as const,
      nodeId: "n3:2",
      expect: [{ kind: "node" as const, name: "Text editor" }],
    };

    await typeTool(requester(request), args);

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "type", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("extends the host deadline with the text length for a keys sequence", async () => {
    const request = vi.fn().mockResolvedValue(typeResult);
    const text = "a".repeat(20_000);

    await typeTool(requester(request), { text, method: "keys" });

    // 1 ms per UTF-16 code unit plus the shared grace margin, and never
    // shorter than a normal host request.
    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "type", text, method: "keys" },
      20_000 + 2_000,
    );
  });

  it("keeps the normal host deadline for paste and value", async () => {
    for (const method of ["paste", "value"] as const) {
      const request = vi.fn().mockResolvedValue(typeResult);

      await typeTool(requester(request), { text: "a".repeat(20_000), method });

      expect(request).toHaveBeenCalledWith(
        "action",
        { kind: "type", text: "a".repeat(20_000), method },
        HOST_REQUEST_TIMEOUT_MS,
      );
    }
  });

  it("maps a failed expect predicate to a non-retryable envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("EXPECTATION_FAILED", "The foreground predicate failed."),
    );

    const result = await typeTool(requester(request), { text: "Hello" });

    expect(result).toEqual({
      ok: false,
      code: "EXPECTATION_FAILED",
      message: "The foreground predicate failed.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the action result schema", async () => {
    const request = vi.fn().mockResolvedValue({ action: "type", method: "paste" });

    const result = await typeTool(requester(request), { text: "Hello" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("type");
  });
});

describe("computer_type MCP tool", () => {
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
    options: McpServerOptions = {},
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

  it("forwards the type text and passes the method through when provided", async () => {
    const request = vi.fn().mockResolvedValue(typeResult);
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_type",
      arguments: { text: "Hello", method: "value" },
    });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "type", text: "Hello", method: "value" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual({ ok: true, data: typeResult });
  });

  it("audits only the UTF-8 length and SHA-256 of the typed text", async () => {
    const text = "héllo wörld";
    const dir = temporaryDirectory();
    const log = new AuditLog({ dir, retentionDays: 7, now: () => FIXED_NOW });
    const request = vi.fn().mockResolvedValue(typeResult);
    const client = await connect(providerFor({ request }), {
      audit: log,
      sessionId: "s_type",
    });

    const result = await client.callTool({
      name: "computer_type",
      arguments: { text, nodeId: "n3:2" },
    });

    expect(result.isError).toBe(false);
    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "type", text, nodeId: "n3:2" },
      HOST_REQUEST_TIMEOUT_MS,
    );

    const line = JSON.parse(
      readFileSync(path.join(dir, "2026-11-10.jsonl"), "utf8").trim(),
    ) as Record<string, unknown>;

    expect(line).toMatchObject({
      timestamp: "2026-11-10T12:00:00.000Z",
      sessionId: "s_type",
      tool: "computer_type",
      nodeId: "n3:2",
      result: "ok",
      textLength: Buffer.byteLength(text, "utf8"),
      textSha256: createHash("sha256").update(text, "utf8").digest("hex"),
    });
    expect(line).not.toHaveProperty("text");
    expect(JSON.stringify(line)).not.toContain("héllo");
  });

  it("rejects missing text as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_type", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects empty text as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_type", arguments: { text: "" } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects an unsupported method as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_type",
        arguments: { text: "Hello", method: "clipboard" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_type",
        arguments: { text: "Hello", keys: "Enter" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects text longer than 100,000 characters as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_type",
        arguments: { text: "a".repeat(100_001) },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
