import type { WindowInfo } from "@kennect/shared";
import { WindowInfoSchema } from "@kennect/shared/schemas";
import { z } from "zod";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { HostError } from "../errors.js";
import { failure, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

/** The focus action payload: `{ window }` (R18). */
export const FocusActionResultSchema = z
  .object({
    window: WindowInfoSchema,
  })
  .strict();

export interface FocusArgs {
  windowId: string;
}

export interface FocusData {
  window: WindowInfo;
}

/**
 * `computer_focus`: asks the host to bring a window to the foreground through
 * the `action` method and returns `{ ok: true, data: { window } }` (R18).
 * The host verifies the foreground and retries once before `FOCUS_REFUSED`.
 * Node-level focus arrives with M2 actions.
 */
export async function focusTool(
  host: HostRequester,
  args: FocusArgs,
): Promise<ToolResult<FocusData>> {
  try {
    const payload = await host.request<unknown>(
      "action",
      { kind: "focus", windowId: args.windowId },
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = FocusActionResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a focus result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok({ window: parsed.data.window });
  } catch (error) {
    if (error instanceof HostError) {
      const retryable = error.code === "HOST_UNAVAILABLE" || error.code === "ACTION_TIMEOUT";
      return failure(error.code, error.message, { retryable });
    }

    return failure(
      "HOST_UNAVAILABLE",
      error instanceof Error && error.message.length > 0
        ? error.message
        : "The focus request failed.",
      { retryable: true },
    );
  }
}
