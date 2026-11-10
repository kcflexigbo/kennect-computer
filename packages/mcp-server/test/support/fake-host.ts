import { randomBytes } from "node:crypto";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import net from "node:net";
import { tmpdir } from "node:os";
import path from "node:path";

import { PROTOCOL_VERSION, type HelloResult } from "@kennect/shared";

import { readStatusFixture } from "./fixture.js";

const MAX_FRAME_BYTES = 16 * 1024 * 1024;

export interface FakeHostOptions {
  /** When false, `status` requests are received but never answered. */
  respondToStatus?: boolean;
  /** Drop the connection instead of answering the named method. */
  dropAfterRequest?: string | null;
  /** Client-side token path override; the fake host still validates its generated token. */
  tokenPath?: string;
  /** Protocol version advertised by `hello` (defaults to the real one). */
  helloProtocolVersion?: number;
  /** Answer `hello` with an UNAUTHORIZED JSON-RPC error. */
  rejectHello?: boolean;
  /** Write the framed response one byte at a time to exercise the client decoder. */
  fragmentResponses?: boolean;
  /** Delay before answering `status`, in milliseconds. */
  statusDelayMs?: number;
  /** Override the status result payload. */
  statusPayload?: unknown;
  /** Answer `status` with a JSON-RPC error carrying this machine code and data. */
  statusError?: { code: string; message: string; data?: Record<string, unknown> };
  /** Write `session.json` into the temp directory (defaults to true). */
  writeSession?: boolean;
}

export interface FakeHost {
  readonly dir: string;
  readonly pipePath: string;
  readonly tokenPath: string;
  readonly token: string;
  readonly statusRequests: number;
  readonly maxConcurrentStatus: number;
  close(): Promise<void>;
}

class FrameDecoder {
  private buffer = Buffer.alloc(0);

  push(chunk: Buffer): unknown[] {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    const messages: unknown[] = [];

    while (this.buffer.length >= 4) {
      const length = this.buffer.readInt32LE(0);
      if (length < 0 || length > MAX_FRAME_BYTES) {
        throw new Error(`invalid frame length ${length}`);
      }

      if (this.buffer.length < 4 + length) {
        break;
      }

      const payload = this.buffer.subarray(4, 4 + length).toString("utf8");
      this.buffer = this.buffer.subarray(4 + length);
      messages.push(JSON.parse(payload));
    }

    return messages;
  }
}

function encodeFrame(message: unknown): Buffer {
  const payload = Buffer.from(JSON.stringify(message), "utf8");
  const header = Buffer.alloc(4);
  header.writeInt32LE(payload.length, 0);
  return Buffer.concat([header, payload]);
}

function fakePipePath(dir: string): string {
  const id = randomBytes(8).toString("hex");
  return process.platform === "win32"
    ? `\\\\.\\pipe\\kennect-mcp-test-${id}`
    : path.join(dir, `host-${id}.sock`);
}

class FakeHostServer implements FakeHost {
  readonly dir: string;
  readonly pipePath: string;
  readonly tokenPath: string;
  readonly token: string;
  readonly statusPayload: unknown;

  private readonly options: FakeHostOptions;
  private readonly server: net.Server;
  private readonly sockets = new Set<net.Socket>();
  private statusRequestCount = 0;
  private activeStatus = 0;
  private peakConcurrentStatus = 0;
  private closed = false;

  constructor(dir: string, token: string, tokenPath: string, options: FakeHostOptions) {
    this.dir = dir;
    this.token = token;
    this.tokenPath = tokenPath;
    this.options = options;
    this.pipePath = fakePipePath(dir);
    this.statusPayload = options.statusPayload ?? readStatusFixture();
    this.server = net.createServer((socket) => this.handleConnection(socket));
  }

  get statusRequests(): number {
    return this.statusRequestCount;
  }

  get maxConcurrentStatus(): number {
    return this.peakConcurrentStatus;
  }

  async listen(): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const onError = (error: Error) => reject(error);
      this.server.once("error", onError);
      this.server.listen(this.pipePath, () => {
        this.server.off("error", onError);
        resolve();
      });
    });
  }

  async close(): Promise<void> {
    if (this.closed) {
      return;
    }
    this.closed = true;
    for (const socket of this.sockets) {
      socket.destroy();
    }
    await new Promise<void>((resolve) => this.server.close(() => resolve()));
    await rm(this.dir, { recursive: true, force: true });
  }

  private handleConnection(socket: net.Socket): void {
    this.sockets.add(socket);
    socket.on("close", () => this.sockets.delete(socket));
    socket.on("error", () => undefined);

    const decoder = new FrameDecoder();
    socket.on("data", (chunk: Buffer) => {
      let messages: unknown[];
      try {
        messages = decoder.push(chunk);
      } catch {
        socket.destroy();
        return;
      }

      for (const message of messages) {
        void this.respond(socket, message);
      }
    });
  }

  private async respond(socket: net.Socket, message: unknown): Promise<void> {
    if (message === null || typeof message !== "object") {
      return;
    }

    const { id, method } = message as { id?: unknown; method?: unknown };
    if (typeof method !== "string") {
      return;
    }

    if (method === "hello") {
      if (this.options.rejectHello) {
        await this.send(socket, {
          jsonrpc: "2.0",
          id,
          error: {
            code: -32001,
            message: "The session token is missing, stale, or invalid.",
            data: { code: "UNAUTHORIZED" },
          },
        });
        return;
      }

      const result: HelloResult = {
        hostVersion: "0.1.0",
        protocolVersion: this.options.helloProtocolVersion ?? PROTOCOL_VERSION,
        platform: "windows",
        pid: process.pid,
      };
      await this.send(socket, { jsonrpc: "2.0", id, result });
      return;
    }

    if (method === "status") {
      this.statusRequestCount += 1;

      if (this.options.dropAfterRequest === "status") {
        socket.destroy();
        return;
      }

      if (this.options.respondToStatus === false) {
        return;
      }

      if (this.options.statusError !== undefined) {
        const { code, message, data } = this.options.statusError;
        await this.send(socket, {
          jsonrpc: "2.0",
          id,
          error: {
            code: -32000,
            message,
            data: { ...data, code },
          },
        });
        return;
      }

      const delay = this.options.statusDelayMs ?? 0;
      this.activeStatus += 1;
      this.peakConcurrentStatus = Math.max(this.peakConcurrentStatus, this.activeStatus);
      try {
        if (delay > 0) {
          await new Promise<void>((resolve) => setTimeout(resolve, delay));
        }
        await this.send(socket, {
          jsonrpc: "2.0",
          id,
          result: this.statusPayload,
        });
      } finally {
        this.activeStatus -= 1;
      }
      return;
    }

    await this.send(socket, {
      jsonrpc: "2.0",
      id,
      error: {
        code: -32601,
        message: `Method not found: ${method}`,
        data: { code: "METHOD_NOT_FOUND" },
      },
    });
  }

  private async send(socket: net.Socket, message: unknown): Promise<void> {
    const frame = encodeFrame(message);
    if (!this.options.fragmentResponses) {
      socket.write(frame);
      return;
    }

    for (let index = 0; index < frame.length; index += 1) {
      socket.write(frame.subarray(index, index + 1));
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
  }
}

/**
 * Starts an in-process stand-in for the Windows host over a real named pipe
 * (or Unix socket off Windows), with the same framing and JSON-RPC handshake.
 */
export async function startFakeHost(options: FakeHostOptions = {}): Promise<FakeHost> {
  const dir = await mkdtemp(path.join(tmpdir(), "kennect-mcp-test-"));
  const token = randomBytes(32).toString("base64url");

  // The fake always knows its generated token; `options.tokenPath` only changes
  // what the client is told to read.
  await writeFile(path.join(dir, "session.token"), token, "utf8");
  const tokenPath = options.tokenPath ?? path.join(dir, "session.token");

  const host = new FakeHostServer(dir, token, tokenPath, options);
  await host.listen();

  if (options.writeSession !== false) {
    await writeFile(
      path.join(dir, "session.json"),
      JSON.stringify({
        pipeName: host.pipePath,
        pid: process.pid,
        protocolVersion: PROTOCOL_VERSION,
        startedAt: new Date().toISOString(),
      }),
      "utf8",
    );
  }

  return host;
}
