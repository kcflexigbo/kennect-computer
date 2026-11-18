import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { ActionResult } from "@kennect/shared/actions";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import { clickTool } from "../src/tools/click.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { notepadWindow } from "./support/snapshot-fixture.js";

const clickResult: ActionResult = {
  window: notepadWindow,
  action: "click",
  method: "mouse",
  needsObservation: true,
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("clickTool", () => {
  it("sends the click action and returns the validated result", async () => {
    const request = vi.fn().mockResolvedValue(clickResult);

    const result = await clickTool(requester(request), { nodeId: "n3:2" });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "click", nodeId: "n3:2" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: clickResult });
  });

  it("forwards coordinates, space, button, count, and expect predicates", async () => {
    const request = vi.fn().mockResolvedValue(clickResult);
    const args = {
      x: 120.5,
      y: 40,
      space: "screen" as const,
      button: "right" as const,
      count: 2,
      expect: [{ kind: "foreground" as const }],
    };

    await clickTool(requester(request), args);

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "click", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("maps host action failures to a non-retryable envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("STALE_SNAPSHOT", "The node is from an older snapshot."),
    );

    const result = await clickTool(requester(request), { nodeId: "n3:2" });

    expect(result).toEqual({
      ok: false,
      code: "STALE_SNAPSHOT",
      message: "The node is from an older snapshot.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the action result schema", async () => {
    const request = vi.fn().mockResolvedValue({ window: notepadWindow, action: "click" });

    const result = await clickTool(requester(request), { nodeId: "n3:2" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("click");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await clickTool(requester(request), { nodeId: "n3:2" });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_click MCP tool", () => {
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

  it("forwards the click request and returns the envelope as structuredContent plus text", async () => {
    const request = vi.fn().mockResolvedValue(clickResult);
    const client = await connect(providerFor({ request }));
    const expected = { ok: true, data: clickResult };

    const result = await client.callTool({
      name: "computer_click",
      arguments: { nodeId: "n3:2", button: "left" },
    });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "click", nodeId: "n3:2", button: "left" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("rejects a click with both a nodeId and coordinates as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_click",
        arguments: { nodeId: "n3:2", x: 1, y: 1 },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a click without a target as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({ name: "computer_click", arguments: { button: "left" } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_click",
        arguments: { nodeId: "n3:2", keys: "Enter" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a count above ten as JSON-RPC -32602", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    await expect(
      client.callTool({
        name: "computer_click",
        arguments: { nodeId: "n3:2", count: 11 },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
