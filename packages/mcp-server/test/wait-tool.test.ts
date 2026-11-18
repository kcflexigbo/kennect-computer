import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { WaitResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { waitTool } from "../src/tools/wait.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadWindow } from "./support/snapshot-fixture.js";

const windowWaitResult: WaitResult = {
  window: notepadWindow,
  condition: { kind: "window", process: "notepad" },
  elapsedMs: 120,
};

const textWaitResult: WaitResult = {
  condition: { kind: "text", name: "Text editor", contains: "Ready" },
  elapsedMs: 250,
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("waitTool", () => {
  it("forwards the condition, inversion, and bound timings", async () => {
    const request = vi.fn().mockResolvedValue(textWaitResult);
    const args = {
      condition: {
        kind: "text" as const,
        name: "Text editor",
        contains: "Ready",
      },
      present: false,
      timeoutMs: 500,
      pollMs: 50,
    };

    const result = await waitTool(requester(request), args);

    expect(request).toHaveBeenCalledWith("wait", args, HOST_REQUEST_TIMEOUT_MS);
    expect(result).toEqual({ ok: true, data: textWaitResult });
  });

  it("returns a window condition's matched window and elapsed time", async () => {
    const request = vi.fn().mockResolvedValue(windowWaitResult);

    const result = await waitTool(requester(request), {
      condition: { kind: "window", process: "notepad" },
    });

    expect(request).toHaveBeenCalledWith(
      "wait",
      { condition: { kind: "window", process: "notepad" } },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: windowWaitResult });
  });

  it("maps a wait timeout to a non-retryable envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("ACTION_TIMEOUT", "The wait timed out after 500 ms."),
    );

    const result = await waitTool(requester(request), {
      condition: { kind: "node", name: "Save" },
      timeoutMs: 500,
    });

    expect(result).toEqual({
      ok: false,
      code: "ACTION_TIMEOUT",
      message: "The wait timed out after 500 ms.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the wait result schema", async () => {
    const request = vi.fn().mockResolvedValue({
      condition: { kind: "window", process: "notepad" },
    });

    const result = await waitTool(requester(request), {
      condition: { kind: "window", process: "notepad" },
    });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("wait");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await waitTool(requester(request), {
      condition: { kind: "window", process: "notepad" },
    });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_wait MCP tool", () => {
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

  it("forwards the wait request and returns the envelope as structuredContent plus text", async () => {
    const request = vi.fn().mockResolvedValue(windowWaitResult);
    const client = await connect(providerFor({ request }));
    const expected = { ok: true, data: windowWaitResult };

    const result = await client.callTool({
      name: "computer_wait",
      arguments: {
        condition: { kind: "window", process: "notepad" },
        timeoutMs: 500,
      },
    });

    expect(request).toHaveBeenCalledWith(
      "wait",
      { condition: { kind: "window", process: "notepad" }, timeoutMs: 500 },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("forwards an inverted text condition", async () => {
    const request = vi.fn().mockResolvedValue(textWaitResult);
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_wait",
      arguments: {
        condition: { kind: "text", identifier: "status", contains: "Ready" },
        present: false,
        timeoutMs: 500,
        pollMs: 50,
      },
    });

    expect(result.isError).toBe(false);
    expect(request).toHaveBeenCalledWith(
      "wait",
      {
        condition: { kind: "text", identifier: "status", contains: "Ready" },
        present: false,
        timeoutMs: 500,
        pollMs: 50,
      },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("rejects a missing condition as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_wait", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a window condition without a process or title as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_wait",
        arguments: { condition: { kind: "window" } },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a text condition without contains as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_wait",
        arguments: { condition: { kind: "text", name: "Text editor" } },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_wait",
        arguments: {
          condition: { kind: "window", process: "notepad" },
          title: "Untitled - Notepad",
        },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
