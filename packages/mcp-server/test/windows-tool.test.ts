import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { WindowInfo } from "@kennect/shared";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { windowsTool } from "../src/tools/windows.js";

export const notepadWindow: WindowInfo = {
  windowId: "w_1",
  title: "Untitled - Notepad",
  processName: "notepad",
  pid: 4242,
  bounds: { x: 0, y: 0, width: 800, height: 600 },
  focused: true,
  minimized: false,
  visible: true,
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("windowsTool", () => {
  it("sends windows.list and returns the windows array", async () => {
    const request = vi.fn().mockResolvedValue({ windows: [notepadWindow] });

    const result = await windowsTool(requester(request));

    expect(request).toHaveBeenCalledWith("windows.list", {}, HOST_REQUEST_TIMEOUT_MS);
    expect(result).toEqual({ ok: true, data: { windows: [notepadWindow] } });
    expect(result.data?.windows[0]?.windowId).toBe("w_1");
  });

  it("rejects a payload that does not match the protocol schema", async () => {
    const request = vi.fn().mockResolvedValue({ windows: [{ windowId: "w_1", title: "Notepad" }] });

    const result = await windowsTool(requester(request));

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("windows");
  });

  it("rejects a payload that leaks a native handle", async () => {
    const request = vi.fn().mockResolvedValue({
      windows: [{ ...notepadWindow, hwnd: 4242 }],
    });

    const result = await windowsTool(requester(request));

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
  });

  it("maps ACTION_TIMEOUT to a retryable isError envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("ACTION_TIMEOUT", "The host did not answer; it was not retried."),
    );

    const result = await windowsTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "ACTION_TIMEOUT",
      message: "The host did not answer; it was not retried.",
      retryable: true,
    });
  });

  it("maps UNAUTHORIZED to a non-retryable isError envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("UNAUTHORIZED", "The session token was rejected."),
    );

    const result = await windowsTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "UNAUTHORIZED",
      message: "The session token was rejected.",
      retryable: false,
    });
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await windowsTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});

describe("computer_windows MCP tool", () => {
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

  it("advertises computer_windows as a read-only tool with a strict empty schema", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ windows: [notepadWindow] }) }),
    );

    const { tools } = await client.listTools();
    const tool = tools.find((candidate) => candidate.name === "computer_windows");

    expect(tool).toMatchObject({
      name: "computer_windows",
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
      },
      inputSchema: {
        type: "object",
        properties: {},
        additionalProperties: false,
      },
    });
    expect(tool?.description).toContain("read-only");
  });

  it("returns the envelope as structuredContent plus a JSON text block", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ windows: [notepadWindow] }) }),
    );

    const result = await client.callTool({ name: "computer_windows", arguments: {} });
    const expected = { ok: true, data: { windows: [notepadWindow] } };

    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ windows: [notepadWindow] }) }),
    );

    await expect(
      client.callTool({ name: "computer_windows", arguments: { unexpected: true } }),
    ).rejects.toMatchObject({ code: -32602 });
  });
});
