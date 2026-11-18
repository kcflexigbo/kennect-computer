import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { FindResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { findTool } from "../src/tools/find.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadSnapshot, notepadWindow } from "./support/snapshot-fixture.js";

const findResult: FindResult = {
  window: notepadWindow,
  generation: 4,
  matches: notepadSnapshot.nodes,
  scanned: 9,
  truncated: false,
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("findTool", () => {
  it("sends the find request and returns the validated result", async () => {
    const request = vi.fn().mockResolvedValue(findResult);

    const result = await findTool(requester(request), {
      text: "File",
      role: "text",
      maxResults: 5,
    });

    expect(request).toHaveBeenCalledWith(
      "find",
      { text: "File", role: "text", maxResults: 5 },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: findResult });
  });

  it("sends an empty request when no filters are given", async () => {
    const request = vi.fn().mockResolvedValue(findResult);

    const result = await findTool(requester(request), {});

    expect(request).toHaveBeenCalledWith("find", {}, HOST_REQUEST_TIMEOUT_MS);
    expect(result).toEqual({ ok: true, data: findResult });
  });

  it("returns an empty match list as a successful result", async () => {
    const empty: FindResult = { ...findResult, matches: [], scanned: 3 };
    const request = vi.fn().mockResolvedValue(empty);

    const result = await findTool(requester(request), { text: "Missing" });

    expect(result).toEqual({ ok: true, data: empty });
  });

  it("maps host failures to the shared envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("STALE_SNAPSHOT", "The window has changed."),
    );

    const result = await findTool(requester(request), { text: "File" });

    expect(result).toEqual({
      ok: false,
      code: "STALE_SNAPSHOT",
      message: "The window has changed.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the find result schema", async () => {
    const request = vi.fn().mockResolvedValue({
      window: notepadWindow,
      generation: 4,
    });

    const result = await findTool(requester(request), { text: "File" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("find");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await findTool(requester(request), { text: "File" });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_find MCP tool", () => {
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

  async function connect(provider: HostProvider): Promise<Client> {
    const server = createMcpServer(provider);
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport);

    const client = new Client({ name: "test-client", version: "1.0.0" });
    await client.connect(clientTransport);

    servers.push(server);
    clients.push(client);
    return client;
  }

  it("forwards the find request and returns the envelope as structuredContent plus text", async () => {
    const request = vi.fn().mockResolvedValue(findResult);
    const client = await connect(providerFor({ request }));
    const expected = { ok: true, data: findResult };

    const result = await client.callTool({
      name: "computer_find",
      arguments: { text: "File", maxResults: 5 },
    });

    expect(request).toHaveBeenCalledWith(
      "find",
      { text: "File", maxResults: 5 },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("rejects an empty text filter as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_find", arguments: { text: "" } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a non-positive maxResults as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_find", arguments: { maxResults: 0 } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_find",
        arguments: { text: "File", nodeId: "n3:2" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
