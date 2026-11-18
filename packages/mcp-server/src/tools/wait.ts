import type { WaitRequest, WaitResult } from "@kennect/shared/actions";
import { WaitResultSchema } from "@kennect/shared/action-schemas";

import {
  effectiveWaitTimeoutMs,
  HOST_REQUEST_GRACE_MS,
  HOST_REQUEST_TIMEOUT_MS,
} from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type WaitArgs = WaitRequest;

/**
 * `computer_wait`: asks the host's `wait` method to poll until a window, node,
 * or node text appears (or disappears when `present` is false), within the
 * request's deadline. Returns `{ ok: true, data: WaitResult }` (R18); a
 * deadline returns an `ACTION_TIMEOUT` envelope.
 */
export async function waitTool(
  host: HostRequester,
  args: WaitArgs,
): Promise<ToolResult<WaitResult>> {
  try {
    const payload = await host.request<unknown>(
      "wait",
      args,
      waitRequestTimeoutMs(args),
    );
    const parsed = WaitResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a wait result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}

/**
 * The host-request deadline for a wait: the host's own deadline plus a margin,
 * never below a normal request. Without this, the transport's timer would fire
 * at (or before) the host's deadline and mask the host's condition-naming
 * `ACTION_TIMEOUT` as a transport timeout.
 */
function waitRequestTimeoutMs(args: WaitArgs): number {
  return Math.max(
    HOST_REQUEST_TIMEOUT_MS,
    effectiveWaitTimeoutMs(args.timeoutMs) + HOST_REQUEST_GRACE_MS,
  );
}
