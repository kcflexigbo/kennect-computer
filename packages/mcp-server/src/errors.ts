import { ERROR_CODES, type ErrorCode } from "@kennect/shared";

const ERROR_CODE_SET: ReadonlySet<string> = new Set(ERROR_CODES);

/** True when a host-provided value is one of the protocol's machine-readable codes. */
export function isErrorCode(value: unknown): value is ErrorCode {
  return typeof value === "string" && ERROR_CODE_SET.has(value);
}

export interface HostErrorOptions {
  retryable?: boolean;
  cause?: unknown;
  /** Structured JSON-RPC error data, e.g. ambiguous-target candidates (R20). */
  data?: unknown;
}

/**
 * An operational failure with a protocol error code. Invalid caller input is
 * reported separately as JSON-RPC -32602; this type is for transport, auth, and
 * action failures that become an `isError` tool result.
 */
export class HostError extends Error {
  readonly code: ErrorCode;
  readonly retryable: boolean;
  /** Structured details supplied by the host, such as ambiguity candidates. */
  readonly data?: unknown;

  constructor(code: ErrorCode, message: string, options: HostErrorOptions = {}) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = "HostError";
    this.code = code;
    this.retryable = options.retryable ?? defaultRetryable(code);
    this.data = options.data;
  }
}

/** Retry is safe by default only when the connection itself was unavailable. */
export function defaultRetryable(code: ErrorCode): boolean {
  return code === "HOST_UNAVAILABLE";
}

export function errorCodeOf(error: unknown): ErrorCode | undefined {
  return error instanceof HostError ? error.code : undefined;
}

/** Normalizes an unknown thrown value into a HostError without losing a real code. */
export function toHostError(error: unknown, fallbackMessage: string): HostError {
  if (error instanceof HostError) {
    return error;
  }

  const message = error instanceof Error && error.message.length > 0
    ? error.message
    : fallbackMessage;

  return new HostError("HOST_UNAVAILABLE", message, { cause: error });
}
