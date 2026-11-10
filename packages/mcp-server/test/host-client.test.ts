import { randomUUID } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import type { HostStatus } from "@kennect/shared";
import { afterEach, describe, expect, it } from "vitest";

import { HostClient } from "../src/host-client.js";
import { startFakeHost, type FakeHost } from "./support/fake-host.js";
import { readStatusFixture } from "./support/fixture.js";

describe("HostClient", () => {
  let fake: FakeHost | undefined;
  let client: HostClient | undefined;

  afterEach(async () => {
    client?.close();
    client = undefined;
    await fake?.close();
    fake = undefined;
  });

  it("performs the hello handshake and correlates a status response", async () => {
    fake = await startFakeHost();
    client = new HostClient(fake.pipePath, fake.tokenPath);

    const hello = await client.connect();
    expect(hello).toMatchObject({
      protocolVersion: 1,
      platform: "windows",
      pid: process.pid,
    });

    const status = await client.request<HostStatus>("status", {});
    expect(status).toEqual(readStatusFixture());
    expect(fake.statusRequests).toBe(1);
  });

  it("fails closed when the token is missing", async () => {
    fake = await startFakeHost({
      tokenPath: path.join(tmpdir(), `missing-${randomUUID()}.token`),
    });
    client = new HostClient(fake.pipePath, fake.tokenPath);

    await expect(client.connect()).rejects.toMatchObject({ code: "UNAUTHORIZED" });
  });

  it("fails closed when the token is malformed", async () => {
    fake = await startFakeHost();
    const malformedPath = path.join(fake.dir, "malformed.token");
    await writeFile(malformedPath, "not-a-valid-token", "utf8");
    client = new HostClient(fake.pipePath, malformedPath);

    await expect(client.connect()).rejects.toMatchObject({ code: "UNAUTHORIZED" });
  });

  it("maps a rejected hello to UNAUTHORIZED", async () => {
    fake = await startFakeHost({ rejectHello: true });
    client = new HostClient(fake.pipePath, fake.tokenPath);

    await expect(client.connect()).rejects.toMatchObject({ code: "UNAUTHORIZED" });
  });

  it("correlates responses and rejects on timeout", async () => {
    fake = await startFakeHost({ respondToStatus: false });
    client = new HostClient(fake.pipePath, fake.tokenPath);
    await client.connect();

    await expect(client.request("status", {}, 50)).rejects.toMatchObject({
      code: "ACTION_TIMEOUT",
    });
  });

  it("does not replay an in-flight request after the host drops", async () => {
    fake = await startFakeHost({ dropAfterRequest: "status" });
    client = new HostClient(fake.pipePath, fake.tokenPath);
    await client.connect();

    await expect(client.request("status", {})).rejects.toMatchObject({
      code: "HOST_UNAVAILABLE",
    });
    expect(fake.statusRequests).toBe(1);
  });

  it("preserves structured error data such as ambiguous-target candidates", async () => {
    fake = await startFakeHost({
      statusError: {
        code: "INVALID_REQUEST",
        message: "The target is ambiguous.",
        data: {
          candidates: [{ windowId: "w_1", title: "a.txt - Notepad", pid: 4242 }],
        },
      },
    });
    client = new HostClient(fake.pipePath, fake.tokenPath);
    await client.connect();

    await expect(client.request("status", {})).rejects.toMatchObject({
      code: "INVALID_REQUEST",
      data: {
        candidates: [{ windowId: "w_1", title: "a.txt - Notepad", pid: 4242 }],
      },
    });
  });

  it("rejects when the host speaks a different protocol version", async () => {
    fake = await startFakeHost({ helloProtocolVersion: 2 });
    client = new HostClient(fake.pipePath, fake.tokenPath);

    await expect(client.connect()).rejects.toMatchObject({
      code: "HOST_UNAVAILABLE",
    });
  });

  it("refuses to send a request before the handshake", async () => {
    fake = await startFakeHost();
    client = new HostClient(fake.pipePath, fake.tokenPath);

    await expect(client.request("status", {})).rejects.toMatchObject({
      code: "HOST_UNAVAILABLE",
    });
    expect(fake.statusRequests).toBe(0);
  });

  it("decodes responses that arrive in fragmented frames", async () => {
    fake = await startFakeHost({ fragmentResponses: true });
    client = new HostClient(fake.pipePath, fake.tokenPath);
    await client.connect();

    await expect(client.request<HostStatus>("status", {})).resolves.toEqual(
      readStatusFixture(),
    );
  });

  it("serializes concurrent requests through one queue", async () => {
    fake = await startFakeHost({ statusDelayMs: 20 });
    client = new HostClient(fake.pipePath, fake.tokenPath);
    await client.connect();

    const [first, second] = await Promise.all([
      client.request<HostStatus>("status", {}),
      client.request<HostStatus>("status", {}),
    ]);

    expect(first).toEqual(readStatusFixture());
    expect(second).toEqual(readStatusFixture());
    expect(fake.maxConcurrentStatus).toBe(1);
  });
});
