import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { readStatusFixture } from "./support/fixture.js";

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("computer_status MCP tool", () => {
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

  it("advertises computer_status as a read-only tool with a strict empty schema", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(readStatusFixture()) }),
    );

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

    const status = tools.find((tool) => tool.name === "computer_status");
    expect(status).toMatchObject({
      name: "computer_status",
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
    expect(status?.description).toContain("read-only");
  });

  it("returns the envelope as structuredContent plus a JSON text block", async () => {
    const fixture = readStatusFixture();
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(fixture) }),
    );

    const result = await client.callTool({ name: "computer_status", arguments: {} });
    const expected = { ok: true, data: fixture };

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

    const result = await client.callTool({ name: "computer_status", arguments: {} });

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "The host is not running.",
      retryable: true,
    });
    expect(result.content).toEqual([
      {
        type: "text",
        text: JSON.stringify({
          ok: false,
          code: "HOST_UNAVAILABLE",
          message: "The host is not running.",
          retryable: true,
        }),
      },
    ]);
  });

  it("rejects unknown arguments as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(readStatusFixture()) }),
    );

    await expect(
      client.callTool({ name: "computer_status", arguments: { unexpected: true } }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("advertises the M2 action tools as mutating with strict schemas", async () => {
    const client = await connect(providerFor({ request: vi.fn() }));

    const { tools } = await client.listTools();
    const actionTools = [
      "computer_click",
      "computer_type",
      "computer_key",
      "computer_scroll",
    ];

    for (const name of actionTools) {
      const tool = tools.find((candidate) => candidate.name === name);
      expect(tool?.annotations).toMatchObject({
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      });
      expect(tool?.inputSchema).toMatchObject({
        type: "object",
        additionalProperties: false,
      });
    }

    const click = tools.find((tool) => tool.name === "computer_click");
    expect(click?.inputSchema).toMatchObject({
      properties: {
        nodeId: { type: "string", minLength: 1 },
        x: { type: "number" },
        y: { type: "number" },
        space: { type: "string", enum: ["image", "screen"] },
        button: { type: "string", enum: ["left", "middle", "right"] },
        count: { type: "integer", minimum: 1 },
        expect: { type: "array" },
      },
    });

    const type = tools.find((tool) => tool.name === "computer_type");
    expect(type?.inputSchema).toMatchObject({
      properties: {
        text: { type: "string", minLength: 1 },
        method: { type: "string", enum: ["paste", "value", "keys"] },
        nodeId: { type: "string", minLength: 1 },
        expect: { type: "array" },
      },
      required: ["text"],
    });

    const key = tools.find((tool) => tool.name === "computer_key");
    expect(key?.inputSchema).toMatchObject({
      properties: {
        keys: { type: "string", minLength: 1 },
        expect: { type: "array" },
      },
      required: ["keys"],
    });

    const scroll = tools.find((tool) => tool.name === "computer_scroll");
    expect(scroll?.inputSchema).toMatchObject({
      properties: {
        nodeId: { type: "string", minLength: 1 },
        x: { type: "number" },
        y: { type: "number" },
        space: { type: "string", enum: ["image", "screen"] },
        direction: { type: "string", enum: ["up", "down", "left", "right"] },
        amount: { type: "integer", minimum: 1 },
        expect: { type: "array" },
      },
      required: ["direction"],
    });
  });
});
