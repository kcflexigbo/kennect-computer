import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { ActionResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { keyTool } from "../src/tools/key.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadWindow } from "./support/snapshot-fixture.js";

const keyResult: ActionResult = {
  window: notepadWindow,
  action: "key",
  method: "keys",
  needsObservation: true,
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("keyTool", () => {
  it("sends the key action and returns the validated result", async () => {
    const request = vi.fn().mockResolvedValue(keyResult);

    const result = await keyTool(requester(request), { keys: "Ctrl+Shift+S" });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "key", keys: "Ctrl+Shift+S" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: keyResult });
  });

  it("forwards expect predicates", async () => {
    const request = vi.fn().mockResolvedValue(keyResult);
    const preconditions = [
      { kind: "text" as const, name: "Status", contains: "Saved" },
    ];

    await keyTool(requester(request), { keys: "Enter", expect: preconditions });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "key", keys: "Enter", expect: preconditions },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("maps FOCUS_REFUSED to a non-retryable envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("FOCUS_REFUSED", "Windows refused the foreground change."),
    );

    const result = await keyTool(requester(request), { keys: "Enter" });

    expect(result).toEqual({
      ok: false,
      code: "FOCUS_REFUSED",
      message: "Windows refused the foreground change.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the action result schema", async () => {
    const request = vi.fn().mockResolvedValue({ window: notepadWindow, action: "key" });

    const result = await keyTool(requester(request), { keys: "Enter" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("key");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await keyTool(requester(request), { keys: "Enter" });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_key MCP tool", () => {
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

  it("forwards the key request and returns the envelope", async () => {
    const request = vi.fn().mockResolvedValue(keyResult);
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_key",
      arguments: { keys: "Ctrl+Shift+S" },
    });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "key", keys: "Ctrl+Shift+S" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual({ ok: true, data: keyResult });
  });

  it("rejects missing keys as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_key", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects empty keys as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_key", arguments: { keys: "" } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_key",
        arguments: { keys: "Enter", text: "Hello" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
