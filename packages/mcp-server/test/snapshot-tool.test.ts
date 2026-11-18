import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { Snapshot } from "@kennect/shared";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { formatSnapshotOutline, snapshotTool } from "../src/tools/snapshot.js";
import { notepadSnapshot } from "./support/snapshot-fixture.js";

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("snapshotTool", () => {
  it("sends the window and optional caps to the snapshot method", async () => {
    const request = vi.fn().mockResolvedValue(notepadSnapshot);

    const output = await snapshotTool(requester(request), {
      windowId: "w_1",
      mode: "a11y",
      maxDepth: 20,
      maxNodes: 50,
      image: true,
    });

    expect(request).toHaveBeenCalledWith(
      "snapshot",
      { windowId: "w_1", mode: "a11y", maxDepth: 20, maxNodes: 50, image: true },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(output.result).toEqual({ ok: true, data: { snapshot: notepadSnapshot } });
    expect(output.text).toContain("app: notepad (pid 4242)");
  });

  it("forwards a region crop", async () => {
    const request = vi.fn().mockResolvedValue(notepadSnapshot);

    await snapshotTool(requester(request), {
      windowId: "w_1",
      region: { x: 1, y: 2, width: 30, height: 40 },
    });

    expect(request).toHaveBeenCalledWith(
      "snapshot",
      { windowId: "w_1", region: { x: 1, y: 2, width: 30, height: 40 } },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("omits unspecified optional arguments", async () => {
    const request = vi.fn().mockResolvedValue(notepadSnapshot);

    await snapshotTool(requester(request), { windowId: "w_1" });

    expect(request).toHaveBeenCalledWith(
      "snapshot",
      { windowId: "w_1" },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("rejects a payload that does not match the protocol schema", async () => {
    const request = vi.fn().mockResolvedValue({ window: { windowId: "w_1" } });

    const output = await snapshotTool(requester(request), { windowId: "w_1" });

    expect(output.result.ok).toBe(false);
    expect(output.result.code).toBe("HOST_UNAVAILABLE");
    expect(output.result.retryable).toBe(true);
    expect(output.text).toBe(JSON.stringify(output.result));
  });

  it("maps WINDOW_GONE to a non-retryable failure and a JSON text block", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("WINDOW_GONE", "The window is gone."),
    );

    const output = await snapshotTool(requester(request), { windowId: "w_9" });

    const expected = {
      ok: false,
      code: "WINDOW_GONE",
      message: "The window is gone.",
      retryable: false,
    };
    expect(output.result).toEqual(expected);
    expect(output.text).toBe(JSON.stringify(expected));
  });
});

describe("formatSnapshotOutline", () => {
  it("renders the header and one line per node", () => {
    const text = formatSnapshotOutline(notepadSnapshot, 20);
    const lines = text.split("\n");

    expect(lines).toHaveLength(5 + notepadSnapshot.nodes.length);
    expect(lines[0]).toBe("app: notepad (pid 4242)");
    expect(lines[1]).toContain('window: "Untitled - Notepad"');
    expect(lines[1]).toContain("id w_1");
    expect(lines[1]).toContain("800x600 at 0,0");
    expect(lines[2]).toBe('focused: [n3:2] edit "Text editor"');
    expect(lines[3]).toBe("modal: none");
    expect(lines[4]).toBe("nodes: 2 shown / 2 total (depth 20)");
    expect(lines[5]).toBe('[n3:1] text "File" @ 0,0 100x20');
    expect(lines[6]).toBe(
      '[n3:2] edit "Text editor" id=editor @ 0,20 400x200 '
        + "actions=focus,setValue focused",
    );
  });

  it("reports modal state and truncation", () => {
    const snapshot: Snapshot = { ...notepadSnapshot, modal: true, truncated: true };

    const text = formatSnapshotOutline(snapshot);

    expect(text).toContain("modal: yes");
    expect(text).toContain("(depth 12, truncated)");
  });

  it("renders no focused node when the snapshot has none", () => {
    const snapshot: Snapshot = { ...notepadSnapshot, focusedNodeId: undefined };

    expect(formatSnapshotOutline(snapshot)).toContain("focused: none");
  });

  it("collapses control characters in untrusted names", () => {
    const snapshot: Snapshot = {
      ...notepadSnapshot,
      nodes: [
        {
          ...notepadSnapshot.nodes[0]!,
          name: 'Save\n\t"file"\u0001 now',
        },
      ],
    };

    const lines = formatSnapshotOutline(snapshot).split("\n");

    expect(lines[5]).toBe('[n3:1] text "Save \'file\' now" @ 0,0 100x20');
    expect(lines).toHaveLength(6);
  });
});

describe("computer_snapshot MCP tool", () => {
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

  it("advertises computer_snapshot as a read-only tool with a strict schema", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(notepadSnapshot) }),
    );

    const { tools } = await client.listTools();
    const snapshot = tools.find((tool) => tool.name === "computer_snapshot");

    expect(snapshot).toMatchObject({
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
      },
      inputSchema: {
        type: "object",
        properties: {
          windowId: { type: "string", minLength: 1 },
          mode: { type: "string", enum: ["a11y", "vision", "both"] },
          maxDepth: { type: "integer", minimum: 1 },
          maxNodes: { type: "integer", minimum: 1 },
          image: { type: "boolean" },
          region: {
            type: "object",
            properties: {
              x: { type: "integer", minimum: 0 },
              y: { type: "integer", minimum: 0 },
              width: { type: "integer", minimum: 1 },
              height: { type: "integer", minimum: 1 },
            },
            required: ["x", "y", "width", "height"],
            additionalProperties: false,
          },
        },
        required: ["windowId"],
        additionalProperties: false,
      },
    });
  });

  it("returns the outline as text plus the R18 envelope", async () => {
    const request = vi.fn().mockResolvedValue(notepadSnapshot);
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_snapshot",
      arguments: { windowId: "w_1" },
    });

    expect(request).toHaveBeenCalledWith(
      "snapshot",
      { windowId: "w_1" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual({
      ok: true,
      data: { snapshot: notepadSnapshot },
    });
    expect(result.content).toEqual([
      { type: "text", text: formatSnapshotOutline(notepadSnapshot) },
    ]);
  });

  it("returns operational failures as isError results with the JSON envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("WINDOW_GONE", "The window is gone."),
    );
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_snapshot",
      arguments: { windowId: "w_9" },
    });

    const expected = {
      ok: false,
      code: "WINDOW_GONE",
      message: "The window is gone.",
      retryable: false,
    };
    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("rejects a call without a windowId as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(notepadSnapshot) }),
    );

    await expect(
      client.callTool({ name: "computer_snapshot", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects an unknown mode as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(notepadSnapshot) }),
    );

    await expect(
      client.callTool({
        name: "computer_snapshot",
        arguments: { windowId: "w_1", mode: "ocr" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown snapshot arguments as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue(notepadSnapshot) }),
    );

    await expect(
      client.callTool({
        name: "computer_snapshot",
        arguments: { windowId: "w_1", monitor: "primary" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
