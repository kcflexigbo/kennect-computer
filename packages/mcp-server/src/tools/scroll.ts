import type { ActionResult, ScrollRequest } from "@kennect/shared/actions";
import { ActionResultSchema } from "@kennect/shared/action-schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type ScrollArgs = ScrollRequest;

/**
 * `computer_scroll`: sends a scroll action through the host's `action` method
 * and returns `{ ok: true, data: ActionResult }` (R18).
 */
export async function scrollTool(
  host: HostRequester,
  args: ScrollArgs,
): Promise<ToolResult<ActionResult>> {
  try {
    const payload = await host.request<unknown>(
      "action",
      { kind: "scroll", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = ActionResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a scroll result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}
