import type { HostStatus } from "@kennect/shared";
import { HostStatusSchema } from "@kennect/shared/schemas";

import { STATUS_TIMEOUT_MS } from "../config.js";
import { HostError } from "../errors.js";
import { failure, ok, type ToolResult } from "../tool-result.js";

/** The minimal host surface a tool needs; HostClient and fakes both satisfy it. */
export interface HostRequester {
  request<T>(method: string, params: unknown, timeoutMs?: number): Promise<T>;
}

/**
 * `computer_status`: reads host health and returns `{ ok: true, data }` (R18).
 * Read-only, so a timeout or dropped connection may safely be retried by the
 * caller even though this function never replays it itself.
 */
export async function statusTool(
  host: HostRequester,
): Promise<ToolResult<HostStatus>> {
  try {
    const payload = await host.request<unknown>("status", {}, STATUS_TIMEOUT_MS);
    const parsed = HostStatusSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a status payload that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    if (error instanceof HostError) {
      const retryable = error.code === "HOST_UNAVAILABLE" || error.code === "ACTION_TIMEOUT";
      return failure(error.code, error.message, { retryable });
    }

    return failure(
      "HOST_UNAVAILABLE",
      error instanceof Error && error.message.length > 0
        ? error.message
        : "The status request failed.",
      { retryable: true },
    );
  }
}
