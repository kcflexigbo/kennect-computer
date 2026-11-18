import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { ErrorCode } from "@modelcontextprotocol/sdk/types.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HOST_REQUEST_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { focusTool } from "../src/tools/focus.js";
import { selectTool, type WindowCandidate } from "../src/tools/select.js";
import { notepadSnapshot, notepadWindow } from "./support/snapshot-fixture.js";

const candidates: WindowCandidate[] = [
  { windowId: "w_1", title: "a.txt - Notepad", pid: 4242 },
  { windowId: "w_2", title: "b.txt - Notepad", pid: 4243 },
];

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

/** Answers `window.select` with the window and `snapshot` with the a11y outline. */
function selectAndSnapshotHost(): ReturnType<typeof vi.fn> {
  return vi.fn(async (method: string) => (
    method === "window.select" ? { window: notepadWindow } : notepadSnapshot
  ));
}

describe("selectTool", () => {
  it("selects the window, snapshots it, and returns both", async () => {
    const request = selectAndSnapshotHost();

    const result = await selectTool(requester(request), { windowId: "w_1" });

    expect(request).toHaveBeenNthCalledWith(
      1,
      "window.select",
      { windowId: "w_1" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(request).toHaveBeenNthCalledWith(
      2,
      "snapshot",
      { windowId: "w_1", mode: "a11y" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({
      ok: true,
      data: { window: notepadWindow, snapshot: notepadSnapshot },
    });
  });

  it("passes process and title criteria through, then snapshots the resolved window", async () => {
    const request = selectAndSnapshotHost();

    await selectTool(requester(request), { process: "notepad", title: "Untitled" });

    expect(request).toHaveBeenNthCalledWith(
      1,
      "window.select",
      { process: "notepad", title: "Untitled" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(request).toHaveBeenNthCalledWith(
      2,
      "snapshot",
      { windowId: "w_1", mode: "a11y" },
      HOST_REQUEST_TIMEOUT_MS,
    );
  });

  it("returns INVALID_REQUEST with the candidate list instead of picking one", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("INVALID_REQUEST", "The target is ambiguous.", {
        data: { code: "INVALID_REQUEST", candidates },
      }),
    );

    const result = await selectTool(requester(request), { title: "Notepad" });

    expect(result).toEqual({
      ok: false,
      code: "INVALID_REQUEST",
      message: "The target is ambiguous.",
      retryable: false,
      data: { candidates },
    });
  });

  it("maps WINDOW_GONE to a non-retryable failure", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("WINDOW_GONE", "The window is gone."),
    );

    const result = await selectTool(requester(request), { windowId: "w_9" });

    expect(result).toEqual({
      ok: false,
      code: "WINDOW_GONE",
      message: "The window is gone.",
      retryable: false,
    });
  });

  it("rejects a select payload that does not match the protocol schema", async () => {
    const request = vi.fn().mockResolvedValue({ window: { windowId: "w_1" } });

    const result = await selectTool(requester(request), { windowId: "w_1" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(request).toHaveBeenCalledTimes(1);
  });

  it("rejects a snapshot payload that does not match the protocol schema", async () => {
    const request = selectAndSnapshotHost();
    request.mockImplementation(async (method: string) => (
      method === "window.select" ? { window: notepadWindow } : { nodes: [] }
    ));

    const result = await selectTool(requester(request), { windowId: "w_1" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
  });

  it("preserves a snapshot failure after a successful select", async () => {
    const request = selectAndSnapshotHost();
    request.mockImplementation(async (method: string) => {
      if (method === "window.select") {
        return { window: notepadWindow };
      }

      throw new HostError("WINDOW_GONE", "The window is gone.");
    });

    const result = await selectTool(requester(request), { windowId: "w_1" });

    expect(result).toEqual({
      ok: false,
      code: "WINDOW_GONE",
      message: "The window is gone.",
      retryable: false,
    });
  });
});

describe("focusTool", () => {
  it("sends an action focus request for the window", async () => {
    const request = vi.fn().mockResolvedValue({ window: notepadWindow });

    const result = await focusTool(requester(request), { windowId: "w_1" });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "focus", windowId: "w_1" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result).toEqual({ ok: true, data: { window: notepadWindow } });
  });

  it("preserves FOCUS_REFUSED as a non-retryable failure", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("FOCUS_REFUSED", "Windows refused the foreground change."),
    );

    const result = await focusTool(requester(request), { windowId: "w_1" });

    expect(result).toEqual({
      ok: false,
      code: "FOCUS_REFUSED",
      message: "Windows refused the foreground change.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the protocol schema", async () => {
    const request = vi.fn().mockResolvedValue({ window: null });

    const result = await focusTool(requester(request), { windowId: "w_1" });

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
  });
});

describe("computer_select and computer_focus MCP tools", () => {
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

  it("advertises select, focus, and snapshot as the window tools", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ window: notepadWindow }) }),
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
      "computer_find",
      "computer_wait",
      "computer_read_text",
      "computer_batch",
    ]);

    const select = tools.find((tool) => tool.name === "computer_select");
    expect(select).toMatchObject({
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: true,
      },
      inputSchema: {
        type: "object",
        additionalProperties: false,
      },
    });
    expect(select?.inputSchema.required).toBeUndefined();

    const focus = tools.find((tool) => tool.name === "computer_focus");
    expect(focus).toMatchObject({
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
      },
      inputSchema: {
        type: "object",
        properties: { windowId: { type: "string", minLength: 1 } },
        required: ["windowId"],
        additionalProperties: false,
      },
    });
  });

  it("returns the window and snapshot envelope as structuredContent plus text", async () => {
    const request = selectAndSnapshotHost();
    const client = await connect(providerFor({ request }));
    const expected = {
      ok: true,
      data: { window: notepadWindow, snapshot: notepadSnapshot },
    };

    const result = await client.callTool({
      name: "computer_select",
      arguments: { windowId: "w_1" },
    });

    expect(request).toHaveBeenNthCalledWith(
      1,
      "window.select",
      { windowId: "w_1" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(request).toHaveBeenNthCalledWith(
      2,
      "snapshot",
      { windowId: "w_1", mode: "a11y" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("returns the focus envelope as structuredContent plus a JSON text block", async () => {
    const request = vi.fn().mockResolvedValue({ window: notepadWindow });
    const client = await connect(providerFor({ request }));
    const expected = { ok: true, data: { window: notepadWindow } };

    const result = await client.callTool({
      name: "computer_focus",
      arguments: { windowId: "w_1" },
    });

    expect(request).toHaveBeenCalledWith(
      "action",
      { kind: "focus", windowId: "w_1" },
      HOST_REQUEST_TIMEOUT_MS,
    );
    expect(result.isError).toBe(false);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("returns ambiguity as an isError envelope carrying candidates", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("INVALID_REQUEST", "The target is ambiguous.", {
        data: { code: "INVALID_REQUEST", candidates },
      }),
    );
    const client = await connect(providerFor({ request }));

    const result = await client.callTool({
      name: "computer_select",
      arguments: { title: "Notepad" },
    });

    const expected = {
      ok: false,
      code: "INVALID_REQUEST",
      message: "The target is ambiguous.",
      retryable: false,
      data: { candidates },
    };
    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(expected);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(expected) },
    ]);
  });

  it("rejects a select call with no target as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ window: notepadWindow }) }),
    );

    await expect(
      client.callTool({ name: "computer_select", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects a focus call without a windowId as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ window: notepadWindow }) }),
    );

    await expect(
      client.callTool({ name: "computer_focus", arguments: {} }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });

  it("rejects unknown select arguments as JSON-RPC -32602", async () => {
    const client = await connect(
      providerFor({ request: vi.fn().mockResolvedValue({ window: notepadWindow }) }),
    );

    await expect(
      client.callTool({
        name: "computer_select",
        arguments: { windowId: "w_1", nodeId: "n1:0" },
      }),
    ).rejects.toMatchObject({ code: ErrorCode.InvalidParams });
  });
});
