import type { ActionResult, KeyRequest } from "@kennect/shared/actions";
import { ActionResultSchema } from "@kennect/shared/action-schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type KeyArgs = KeyRequest;

/**
 * `computer_key`: sends a key or chord action through the host's `action`
 * method and returns `{ ok: true, data: ActionResult }` (R18).
 */
export async function keyTool(
  host: HostRequester,
  args: KeyArgs,
): Promise<ToolResult<ActionResult>> {
  try {
    const payload = await host.request<unknown>(
      "action",
      { kind: "key", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = ActionResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a key result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}
