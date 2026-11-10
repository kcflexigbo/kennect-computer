import { readFile } from "node:fs/promises";
import net from "node:net";

import {
  PROTOCOL_VERSION,
  type HelloParams,
  type HelloResult,
} from "@kennect/shared";
import { TOKEN_PATTERN } from "@kennect/shared/schemas";

import {
  CLIENT_NAME,
  CONNECT_TIMEOUT_MS,
  HELLO_TIMEOUT_MS,
  HOST_REQUEST_TIMEOUT_MS,
} from "./config.js";
import { HostError, isErrorCode } from "./errors.js";

const MAX_FRAME_BYTES = 16 * 1024 * 1024;

interface JsonRpcErrorBody {
  code?: unknown;
  message?: unknown;
  data?: { code?: unknown };
}

interface JsonRpcResponse {
  jsonrpc?: unknown;
  id?: unknown;
  result?: unknown;
  error?: JsonRpcErrorBody;
}

interface PendingRequest {
  resolve(value: unknown): void;
  reject(error: Error): void;
  timer: NodeJS.Timeout;
}

export interface HostClientOptions {
  connectTimeoutMs?: number;
  helloTimeoutMs?: number;
  requestTimeoutMs?: number;
  maxFrameBytes?: number;
}

/**
 * JSON-RPC over a length-prefixed local socket, with one authenticated
 * connection per host process.
 *
 * Requests are serialized through a single queue (R19). A request that times
 * out or loses the connection is rejected, never replayed: a mutation may
 * already have run on the host.
 */
export class HostClient {
  private socket: net.Socket | null = null;
  private helloResult: HelloResult | null = null;
  private nextId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  private queue: Promise<unknown> = Promise.resolve();
  private buffer = Buffer.alloc(0);
  private readonly connectTimeoutMs: number;
  private readonly helloTimeoutMs: number;
  private readonly requestTimeoutMs: number;
  private readonly maxFrameBytes: number;

  constructor(
    private readonly pipePath: string,
    private readonly tokenPath: string,
    options: HostClientOptions = {},
  ) {
    this.connectTimeoutMs = options.connectTimeoutMs ?? CONNECT_TIMEOUT_MS;
    this.helloTimeoutMs = options.helloTimeoutMs ?? HELLO_TIMEOUT_MS;
    this.requestTimeoutMs = options.requestTimeoutMs ?? HOST_REQUEST_TIMEOUT_MS;
    this.maxFrameBytes = options.maxFrameBytes ?? MAX_FRAME_BYTES;
  }

  /** Reads the token, connects, and completes the `hello` handshake. */
  async connect(): Promise<HelloResult> {
    if (this.helloResult !== null && this.socket !== null) {
      return this.helloResult;
    }

    const token = await this.readToken();
    const socket = await this.openSocket();
    this.socket = socket;
    this.attachSocket(socket);

    try {
      const hello = await this.exchange<HelloResult>(
        "hello",
        {
          token,
          protocolVersion: PROTOCOL_VERSION,
          clientName: CLIENT_NAME,
        } satisfies HelloParams,
        this.helloTimeoutMs,
      );

      if (
        hello === null
        || typeof hello !== "object"
        || (hello as Partial<HelloResult>).protocolVersion !== PROTOCOL_VERSION
      ) {
        throw new HostError(
          "HOST_UNAVAILABLE",
          `The Kennect host speaks an unsupported protocol version (expected ${PROTOCOL_VERSION}).`,
        );
      }

      this.helloResult = hello;
      return hello;
    } catch (error) {
      this.close();
      throw error;
    }
  }

  /** Sends one request after every previously queued request has settled. */
  request<T>(
    method: string,
    params: unknown,
    timeoutMs: number = this.requestTimeoutMs,
  ): Promise<T> {
    if (this.helloResult === null || this.socket === null) {
      return Promise.reject(
        new HostError(
          "HOST_UNAVAILABLE",
          "The Kennect host connection is not established. Call connect() first.",
        ),
      );
    }

    const run = (): Promise<T> => this.exchange<T>(method, params, timeoutMs);
    const result = this.queue.then(run, run);

    // The queue must survive a failed request so later calls still run.
    this.queue = result.then(
      () => undefined,
      () => undefined,
    );

    return result;
  }

  isConnected(): boolean {
    return (
      this.socket !== null
      && !this.socket.destroyed
      && this.helloResult !== null
    );
  }

  close(): void {
    const socket = this.socket;
    this.socket = null;
    this.helloResult = null;
    this.buffer = Buffer.alloc(0);

    this.rejectPending(
      new HostError(
        "HOST_UNAVAILABLE",
        "The Kennect host connection was closed.",
        { retryable: true },
      ),
    );

    socket?.destroy();
  }

  private async readToken(): Promise<string> {
    let raw: string;
    try {
      raw = await readFile(this.tokenPath, "utf8");
    } catch (error) {
      throw new HostError(
        "UNAUTHORIZED",
        `The session token at ${this.tokenPath} is missing or unreadable. Start the Kennect host, then retry.`,
        { cause: error },
      );
    }

    const token = raw.trim();
    if (!TOKEN_PATTERN.test(token)) {
      throw new HostError(
        "UNAUTHORIZED",
        `The session token at ${this.tokenPath} is malformed. Restart the Kennect host to rotate it.`,
      );
    }

    return token;
  }

  private openSocket(): Promise<net.Socket> {
    return new Promise<net.Socket>((resolve, reject) => {
      const socket = net.connect(this.pipePath);
      let settled = false;

      const fail = (error: Error): void => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(timer);
        socket.removeListener("error", onError);
        socket.destroy();
        reject(error);
      };

      const timer = setTimeout(() => {
        fail(
          new HostError(
            "HOST_UNAVAILABLE",
            `Timed out connecting to the Kennect host pipe after ${this.connectTimeoutMs} ms.`,
          ),
        );
      }, this.connectTimeoutMs);

      const onError = (error: Error): void => {
        fail(
          new HostError(
            "HOST_UNAVAILABLE",
            `Cannot connect to the Kennect host pipe: ${error.message}`,
            { cause: error },
          ),
        );
      };

      socket.once("error", onError);
      socket.once("connect", () => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(timer);
        socket.removeListener("error", onError);
        resolve(socket);
      });
    });
  }

  private attachSocket(socket: net.Socket): void {
    socket.on("data", (chunk: Buffer) => this.onData(chunk));
    // Errors are followed by a close event; the close handler rejects pending work.
    socket.on("error", () => undefined);
    socket.on("close", () => {
      if (this.socket !== socket) {
        return;
      }

      this.socket = null;
      this.helloResult = null;
      this.rejectPending(
        new HostError(
          "HOST_UNAVAILABLE",
          "The Kennect host disconnected while the request was in flight; the request was not retried.",
          { retryable: true },
        ),
      );
    });
  }

  private onData(chunk: Buffer): void {
    this.buffer = Buffer.concat([this.buffer, chunk]);

    while (this.buffer.length >= 4) {
      const length = this.buffer.readInt32LE(0);
      if (length < 0 || length > this.maxFrameBytes) {
        this.failConnection(
          new HostError(
            "HOST_UNAVAILABLE",
            `The host sent a frame with an invalid length (${length} bytes).`,
          ),
        );
        return;
      }

      if (this.buffer.length < 4 + length) {
        return;
      }

      const payload = this.buffer.subarray(4, 4 + length).toString("utf8");
      this.buffer = this.buffer.subarray(4 + length);

      let message: JsonRpcResponse;
      try {
        message = JSON.parse(payload) as JsonRpcResponse;
      } catch {
        this.failConnection(
          new HostError("HOST_UNAVAILABLE", "The host sent a malformed JSON frame."),
        );
        return;
      }

      this.dispatch(message);
    }
  }

  private dispatch(message: JsonRpcResponse): void {
    // Events (no numeric id) are not part of M1; ignore them.
    if (typeof message.id !== "number") {
      return;
    }

    const pending = this.pending.get(message.id);
    if (pending === undefined) {
      // A late response for a timed-out request: never correlate it to newer work.
      return;
    }

    this.pending.delete(message.id);
    clearTimeout(pending.timer);

    if (message.error !== undefined) {
      const code = isErrorCode(message.error.data?.code)
        ? message.error.data.code
        : "INVALID_REQUEST";
      const text = typeof message.error.message === "string"
        ? message.error.message
        : `The host call failed (${code}).`;
      pending.reject(new HostError(code, text));
      return;
    }

    pending.resolve(message.result);
  }

  private exchange<T>(method: string, params: unknown, timeoutMs: number): Promise<T> {
    const socket = this.socket;
    if (socket === null || socket.destroyed) {
      return Promise.reject(
        new HostError(
          "HOST_UNAVAILABLE",
          "The Kennect host connection is not open.",
        ),
      );
    }

    const id = this.nextId;
    this.nextId += 1;

    const payload = Buffer.from(
      JSON.stringify({ jsonrpc: "2.0", id, method, params }),
      "utf8",
    );

    if (payload.length > this.maxFrameBytes) {
      return Promise.reject(
        new HostError(
          "FRAME_TOO_LARGE",
          `The ${method} request exceeds the ${this.maxFrameBytes}-byte frame limit.`,
        ),
      );
    }

    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(
          new HostError(
            "ACTION_TIMEOUT",
            `The Kennect host did not answer ${method} within ${timeoutMs} ms; the request was not retried.`,
          ),
        );
      }, timeoutMs);

      this.pending.set(id, {
        resolve: resolve as (value: unknown) => void,
        reject,
        timer,
      });

      const header = Buffer.alloc(4);
      header.writeInt32LE(payload.length, 0);

      socket.write(Buffer.concat([header, payload]), (error?: Error | null) => {
        if (error === null || error === undefined) {
          return;
        }

        const pending = this.pending.get(id);
        if (pending === undefined) {
          return;
        }

        this.pending.delete(id);
        clearTimeout(pending.timer);
        pending.reject(
          new HostError(
            "HOST_UNAVAILABLE",
            `Failed to send ${method} to the host: ${error.message}`,
            { cause: error },
          ),
        );
      });
    });
  }

  private failConnection(error: HostError): void {
    const socket = this.socket;
    this.socket = null;
    this.helloResult = null;
    this.buffer = Buffer.alloc(0);
    this.rejectPending(error);
    socket?.destroy();
  }

  private rejectPending(error: HostError): void {
    for (const [id, pending] of this.pending) {
      this.pending.delete(id);
      clearTimeout(pending.timer);
      pending.reject(error);
    }
  }
}
