import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { ActionResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { scrollTool } from "../src/tools/scroll.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadWindow } from "./support/snapshot-fixture.js";

const scrollResult: ActionResult = {
  window: notepadWindow,
  action: "scroll",
  method: "wheel",
  needsObservation: true,
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("scrollTool", () => {
  it("sends the scroll action with the node, direction, and amount", async () => {
    const request = vi.fn().mockResolvedValue(scrollResult);

    const result = await scrollTool(requester(request), {
      nodeId: "n3:2",
      direction: "down",
      amount: 3,
    });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "scroll", nodeId: "n3:2", direction: "down", amount: 3 },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: scrollResult });
  });

  it("forwards coordinates, space, and expect predicates", async () => {
    const request = vi.fn().mockResolvedValue(scrollResult);
    const args = {
      x: 10,
      y: 20,
      space: "image" as const,
      direction: "up" as const,
      expect: [{ kind: "foreground" as const }],
    };

    await scrollTool(requester(request), args);

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "scroll", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("maps WINDOW_GONE to a non-retryable envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("WINDOW_GONE", "The window is gone."),
    );

    const result = await scrollTool(requester(request), {
      nodeId: "n3:2",
      direction: "down",
    });

    expect(result).toEqual({
      ok: false,
      code: "WINDOW_GONE",
      message: "The window is gone.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the action result schema", async () => {
    const request = vi.fn().mockResolvedValue({
      window: notepadWindow,
      action: "scroll",
      method: "wheel",
    });

    const result = await scrollTool(requester(request), {
      nodeId: "n3:2",
      direction: "down",
    });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("scroll");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await scrollTool(requester(request), {
      nodeId: "n3:2",
      direction: "down",
    });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_scroll MCP tool", () => {
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

  it("forwards the scroll request and returns the envelope", async () => {
    const request = vi.fn().mockResolvedValue(scrollResult);
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_scroll",
      arguments: { nodeId: "n3:2", direction: "down" },
    });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "scroll", nodeId: "n3:2", direction: "down" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual({ ok: true, data: scrollResult });
  });

  it("rejects missing direction as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_scroll",
        arguments: { nodeId: "n3:2" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects both target forms as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_scroll",
        arguments: { nodeId: "n3:2", x: 1, y: 1, direction: "down" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects no target as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_scroll",
        arguments: { direction: "down" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_scroll",
        arguments: { nodeId: "n3:2", direction: "down", button: "left" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
