import { WindowInfoSchema } from "@kennect/shared/schemas";
import { z } from "zod";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { HostError } from "../errors.js";
import { failure, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

/**
 * The `windows.list` payload. `WindowInfoSchema` is strict, so a host that
 * leaks a native handle or any other platform field fails validation instead
 * of crossing the protocol boundary.
 */
export const WindowsListSchema = z
  .object({
    windows: z.array(WindowInfoSchema),
  })
  .strict();

export type WindowsList = z.infer<typeof WindowsListSchema>;

/**
 * `computer_windows`: lists top-level windows and returns `{ ok: true, data }`
 * (R18). Read-only, so a timeout or dropped connection may safely be retried
 * by the caller even though this function never replays the request itself.
 */
export async function windowsTool(
  host: HostRequester,
): Promise<ToolResult<WindowsList>> {
  try {
    const payload = await host.request<unknown>("windows.list", {}, HOST_REQUEST_TIMEOUT_MS);
    const parsed = WindowsListSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a windows list that does not match protocol v1.",
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
        : "The windows request failed.",
      { retryable: true },
    );
  }
}
