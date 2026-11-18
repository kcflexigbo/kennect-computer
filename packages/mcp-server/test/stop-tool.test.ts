import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { HostClient } from "../src/host-client.js";
import { createMcpServer } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { stopTool } from "../src/tools/stop.js";
import { startFakeHost } from "./support/fake-host.js";

interface FakeHost extends HostRequester {
  readonly calls: Array<[string, unknown]>;
}

function fakeHost(options: { stopResult?: unknown } = {}): FakeHost {
  const calls: Array<[string, unknown]> = [];
  return {
    calls,
    request: async (method, params) => {
      calls.push([method, params]);
      return options.stopResult ?? { ok: true };
    },
  };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("stopTool", () => {
  it("computer_stop sends stop to the host", async () => {
    const host = fakeHost({ stopResult: { ok: true } });

    await expect(stopTool(host)).resolves.toEqual({ ok: true, data: { ok: true } });
    expect(host.calls).toContainEqual(["stop", {}]);
    expect(host.calls).toHaveLength(1);
  });

  it("sends stop with the host request timeout", async () => {
    const request = vi.fn().mockResolvedValue({ ok: true });

    await stopTool({ request });

    expect(request).toHaveBeenCalledWith("stop", {}, HOST_REQUEST_TIMEOUT_MS);
  });

  it("rejects a stop payload that does not match the protocol", async () => {
    const host = fakeHost({ stopResult: { ok: "yes" } });

    const result = await stopTool(host);

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("stop");
  });

  it("maps LOCKED_BY_OTHER_SESSION to a non-retryable failure", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("LOCKED_BY_OTHER_SESSION", "Another client holds the control session."),
    );

    const result = await stopTool({ request });

    expect(result).toEqual({
      ok: false,
      code: "LOCKED_BY_OTHER_SESSION",
      message: "Another client holds the control session.",
      retryable: false,
    });
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await stopTool({ request });

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });

  it("queues behind a slow in-flight request instead of preempting it", async () => {
    // The documented M1 contract: the host reads the next frame only after the
    // running operation returns, and the client serializes requests through one
    // queue. A stop issued during a long snapshot is answered after it, so it
    // cannot abort work that is already in flight; the hotkey/tray path does.
    const fake = await startFakeHost({ statusDelayMs: 200 });
    const client = new HostClient(fake.pipePath, fake.tokenPath);

    try {
      await client.connect();

      const slow = client.request("status", {});
      const stop = stopTool(client);
      let stopSettled = false;
      void stop.then(() => {
        stopSettled = true;
      });

      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(stopSettled).toBe(false);

      await slow;
      await expect(stop).resolves.toEqual({ ok: true, data: { ok: true } });
      expect(stopSettled).toBe(true);
    } finally {
      client.close();
      await fake.close();
    }
  });
});

describe("computer_stop MCP tool", () => {
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

  it("advertises computer_stop with a strict empty schema", async () => {
    const client = await connect(providerFor(fakeHost()));

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
    ]);

    const stop = tools.find((tool) => tool.name === "computer_stop");
    expect(stop).toMatchObject({
      name: "computer_stop",
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
      inputSchema: {
        type: "object",
        properties: {},
        additionalProperties: false,
      },
    });
    expect(stop?.description).toContain("stop");
  });

  it("returns the envelope as structuredContent plus a JSON text block", async () => {
    const client = await connect(providerFor(fakeHost()));
    const expected = { ok: true, data: { ok: true } };

    const result = await client.callTool({ name: "computer_stop", arguments: {} });

    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("returns operational failures as isError results carrying the envelope", async () => {
    const client = await connect({
      ensureHost: async () => {
        throw new HostError("HOST_UNAVAILABLE", "The host is not running.");
      },
    });

    const result = await client.callTool({ name: "computer_stop", arguments: {} });

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "The host is not running.",
      retryable: true,
    });
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(providerFor(fakeHost()));

    await expect(
      client.callTool({ name: "computer_stop", arguments: { windowId: "w_1" } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
