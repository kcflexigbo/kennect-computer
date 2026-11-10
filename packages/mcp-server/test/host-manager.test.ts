import { access, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";

import type { HostStatus } from "@kennect/shared";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HostError } from "../src/errors.js";
import { HostManager, type HostConnection } from "../src/host-manager.js";
import { statusTool } from "../src/tools/status.js";
import { startFakeHost, type FakeHost } from "./support/fake-host.js";
import { readStatusFixture } from "./support/fixture.js";

function createFakeClient(): HostConnection {
  return {
    request: vi.fn(async () => readStatusFixture() as HostStatus),
    close: vi.fn(),
    isConnected: () => true,
  };
}

async function fileExists(filePath: string): Promise<boolean> {
  try {
    await access(filePath);
    return true;
  } catch {
    return false;
  }
}

describe("HostManager", () => {
  const managers: HostManager[] = [];
  const fakes: FakeHost[] = [];
  const directories: string[] = [];

  afterEach(async () => {
    for (const manager of managers.splice(0)) {
      manager.close();
    }
    for (const fake of fakes.splice(0)) {
      await fake.close();
    }
    for (const directory of directories.splice(0)) {
      await rm(directory, { recursive: true, force: true });
    }
  });

  it("respawns the host once when the first connection fails", async () => {
    const spawn = vi.fn().mockResolvedValue(undefined);
    const fakeClient = createFakeClient();
    let attempts = 0;

    const manager = new HostManager({
      spawn,
      readSession: () => null,
      spawnWaitMs: 10,
      connect: async () => {
        if (attempts++ === 0) {
          throw new Error("refused");
        }
        return fakeClient;
      },
    });
    managers.push(manager);

    await expect(manager.ensureHost()).resolves.toBe(fakeClient);
    expect(spawn).toHaveBeenCalledTimes(1);
    expect(attempts).toBe(2);
  });

  it("never spawns a second time when the reconnect also fails", async () => {
    const spawn = vi.fn().mockResolvedValue(undefined);

    const manager = new HostManager({
      spawn,
      readSession: () => null,
      spawnWaitMs: 10,
      connect: async () => {
        throw new HostError("HOST_UNAVAILABLE", "The pipe was refused.");
      },
    });
    managers.push(manager);

    await expect(manager.ensureHost()).rejects.toMatchObject({
      code: "HOST_UNAVAILABLE",
    });
    expect(spawn).toHaveBeenCalledTimes(1);
  });

  it("does not spawn when authentication fails", async () => {
    const spawn = vi.fn().mockResolvedValue(undefined);

    const manager = new HostManager({
      spawn,
      readSession: () => null,
      spawnWaitMs: 10,
      connect: async () => {
        throw new HostError("UNAUTHORIZED", "The token was rejected.");
      },
    });
    managers.push(manager);

    await expect(manager.ensureHost()).rejects.toMatchObject({
      code: "UNAUTHORIZED",
    });
    expect(spawn).not.toHaveBeenCalled();
  });

  it("prunes a stale session.json PID before connecting", async () => {
    const directory = await mkdtemp(path.join(tmpdir(), "kennect-manager-"));
    directories.push(directory);
    const sessionPath = path.join(directory, "session.json");
    await writeFile(
      sessionPath,
      JSON.stringify({
        pipeName: "\\\\.\\pipe\\stale-kennect-host",
        pid: 999_999_999,
        protocolVersion: 1,
        startedAt: new Date(0).toISOString(),
      }),
      "utf8",
    );

    const spawn = vi.fn().mockResolvedValue(undefined);
    const fakeClient = createFakeClient();
    let attempts = 0;
    const manager = new HostManager({
      sessionDir: directory,
      spawn,
      isProcessAlive: () => false,
      spawnWaitMs: 10,
      connect: async () => {
        if (attempts++ === 0) {
          throw new HostError("HOST_UNAVAILABLE", "The pipe was refused.");
        }
        return fakeClient;
      },
    });
    managers.push(manager);

    await expect(manager.ensureHost()).resolves.toBe(fakeClient);
    expect(spawn).toHaveBeenCalledTimes(1);
    expect(await fileExists(sessionPath)).toBe(false);
  });

  it("passes the configured host path to spawn", async () => {
    const spawn = vi.fn().mockResolvedValue(undefined);
    const fakeClient = createFakeClient();
    let attempts = 0;

    const manager = new HostManager({
      hostPath: "C:\\kennect\\KennectComputer.Host.exe",
      spawn,
      readSession: () => null,
      spawnWaitMs: 10,
      connect: async () => {
        if (attempts++ === 0) {
          throw new HostError("HOST_UNAVAILABLE", "The pipe was refused.");
        }
        return fakeClient;
      },
    });
    managers.push(manager);

    await manager.ensureHost();
    expect(spawn).toHaveBeenCalledWith("C:\\kennect\\KennectComputer.Host.exe");
  });

  it("reuses a live connection without spawning or reconnecting", async () => {
    const spawn = vi.fn().mockResolvedValue(undefined);
    const connect = vi.fn(async () => createFakeClient());

    const manager = new HostManager({
      spawn,
      readSession: () => null,
      spawnWaitMs: 10,
      connect,
    });
    managers.push(manager);

    const first = await manager.ensureHost();
    const second = await manager.ensureHost();

    expect(second).toBe(first);
    expect(connect).toHaveBeenCalledTimes(1);
    expect(spawn).not.toHaveBeenCalled();
  });

  it("connects to a fake host through the default session discovery", async () => {
    const fake = await startFakeHost();
    fakes.push(fake);

    const manager = new HostManager({ sessionDir: fake.dir });
    managers.push(manager);

    const host = await manager.ensureHost();
    const result = await statusTool(host);

    expect(result).toEqual({ ok: true, data: readStatusFixture() });
    expect(fake.statusRequests).toBe(1);
  });
});
