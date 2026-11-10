import { HostError } from "./errors.js";

/** Per-step results carried by a future batch tool (R18). */
export interface ToolPartialResult {
  completed: number;
  failedIndex: number;
  results: unknown[];
}

/**
 * The single tool result envelope (R18), emitted as `structuredContent` plus a
 * JSON text block. `ok: false` results are returned with MCP `isError: true`
 * and are never thrown as protocol errors.
 */
export interface ToolResult<T> {
  ok: boolean;
  data?: T;
  code?: string;
  message?: string;
  retryable?: boolean;
  needsObservation?: boolean;
  partial?: ToolPartialResult;
}

export interface FailureOptions {
  retryable?: boolean;
  needsObservation?: boolean;
  partial?: ToolPartialResult;
}

export function ok<T>(data: T): ToolResult<T> {
  return { ok: true, data };
}

export function failure(
  code: string,
  message: string,
  options: FailureOptions = {},
): ToolResult<never> {
  const result: ToolResult<never> = { ok: false, code, message };

  if (options.retryable !== undefined) {
    result.retryable = options.retryable;
  }
  if (options.needsObservation !== undefined) {
    result.needsObservation = options.needsObservation;
  }
  if (options.partial !== undefined) {
    result.partial = options.partial;
  }

  return result;
}

/** Converts any thrown operational failure into the envelope. */
export function failureFromError(
  error: unknown,
  fallbackMessage = "The Kennect host is unavailable.",
): ToolResult<never> {
  if (error instanceof HostError) {
    return failure(error.code, error.message, { retryable: error.retryable });
  }

  const message = error instanceof Error && error.message.length > 0
    ? error.message
    : fallbackMessage;

  return failure("HOST_UNAVAILABLE", message, { retryable: true });
}
