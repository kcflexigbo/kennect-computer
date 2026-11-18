import type { ActionResult, ClickRequest } from "@kennect/shared/actions";
import { ActionResultSchema } from "@kennect/shared/action-schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type ClickArgs = ClickRequest;

/**
 * `computer_click`: sends a click action through the host's `action` method
 * and returns `{ ok: true, data: ActionResult }` (R18). The host resolves a
 * snapshot node ID or coordinates and reports how the click was carried out.
 */
export async function clickTool(
  host: HostRequester,
  args: ClickArgs,
): Promise<ToolResult<ActionResult>> {
  try {
    const payload = await host.request<unknown>(
      "action",
      { kind: "click", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = ActionResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a click result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}
