import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Server } from "@modelcontextprotocol/sdk/server/index.js";
import type { ImagePayload } from "@kennect/shared";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HostError } from "../src/errors.js";
import { createMcpServer } from "../src/index.js";
import type { HostProvider } from "../src/tools.js";
import type { HostRequester } from "../src/tools/status.js";
import { snapshotTool } from "../src/tools/snapshot.js";
import { notepadSnapshot } from "./support/snapshot-fixture.js";

/** A protocol-valid 1x1 PNG payload. */
const imagePayload: ImagePayload = {
  data:
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
  mimeType: "image/png",
  scale: 1.25,
  sourceBounds: { x: 0, y: 0, width: 800, height: 600 },
};

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

function fakeHostWithImage(): HostRequester {
  return requester(vi.fn().mockResolvedValue({ ...notepadSnapshot, image: imagePayload }));
}

function providerFor(host: HostRequester): HostProvider {
  return { ensureHost: async () => host };
}

describe("snapshotTool image content", () => {
  it("returns image content when the host includes a PNG", async () => {
    const result = await snapshotTool(fakeHostWithImage(), { windowId: "w_1", image: true });

    expect(result.content.some((content) => content.type === "image")).toBe(true);
  });

  it("returns the outline text followed by the image block", async () => {
    const result = await snapshotTool(fakeHostWithImage(), { windowId: "w_1", image: true });

    expect(result.content).toEqual([
      { type: "text", text: result.text },
      { type: "image", data: imagePayload.data, mimeType: "image/png" },
    ]);
    expect(result.text).toContain('window: "Untitled - Notepad"');
  });

  it("returns only the outline text when the host omits the image", async () => {
    const result = await snapshotTool(
      requester(vi.fn().mockResolvedValue(notepadSnapshot)),
      { windowId: "w_1" },
    );

    expect(result.content).toEqual([{ type: "text", text: result.text }]);
  });

  it("returns the failure envelope as text content when the request fails", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("WINDOW_GONE", "The window is gone."),
    );

    const result = await snapshotTool(requester(request), { windowId: "w_9" });

    expect(result.result.ok).toBe(false);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(result.result) },
    ]);
  });

  it("rejects an image payload that does not match the protocol", async () => {
    const request = vi.fn().mockResolvedValue({
      ...notepadSnapshot,
      image: { ...imagePayload, mimeType: "image/jpeg" },
    });

    const result = await snapshotTool(requester(request), { windowId: "w_1", image: true });

    expect(result.result.ok).toBe(false);
    expect(result.content).toEqual([
      { type: "text", text: JSON.stringify(result.result) },
    ]);
  });
});

describe("computer_snapshot image content", () => {
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

  it("emits the image block alongside the outline text", async () => {
    const client = await connect(providerFor(fakeHostWithImage()));

    const result = await client.callTool({
      name: "computer_snapshot",
      arguments: { windowId: "w_1", image: true },
    });

    expect(result.isError).toBe(false);
    expect(result.content).toHaveLength(2);
    expect(result.content[0]).toMatchObject({ type: "text" });
    expect(result.content[1]).toEqual({
      type: "image",
      data: imagePayload.data,
      mimeType: "image/png",
    });

    // The structured envelope keeps the image metadata but not the binary:
    // the image content block is the single copy of the PNG bytes.
    expect(result.structuredContent).toEqual({
      ok: true,
      data: {
        snapshot: {
          ...notepadSnapshot,
          image: {
            mimeType: imagePayload.mimeType,
            scale: imagePayload.scale,
            sourceBounds: imagePayload.sourceBounds,
            byteLength: Buffer.from(imagePayload.data, "base64").length,
          },
        },
      },
    });
    const structuredImage = (
      result.structuredContent as {
        data: { snapshot: { image: Record<string, unknown> } };
      }
    ).data.snapshot.image;
    expect(structuredImage).not.toHaveProperty("data");
    expect(JSON.stringify(result.structuredContent)).not.toContain(imagePayload.data);
  });

  it("emits text only when the snapshot has no image", async () => {
    const client = await connect(
      providerFor(requester(vi.fn().mockResolvedValue(notepadSnapshot))),
    );

    const result = await client.callTool({
      name: "computer_snapshot",
      arguments: { windowId: "w_1" },
    });

    expect(result.content).toHaveLength(1);
    expect(result.content[0]).toMatchObject({ type: "text" });
  });
});
