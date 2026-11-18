import type { ActionResult, TypeRequest } from "@kennect/shared/actions";
import { ActionResultSchema } from "@kennect/shared/action-schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type TypeArgs = TypeRequest;

/**
 * `computer_type`: sends a type action through the host's `action` method and
 * returns `{ ok: true, data: ActionResult }` (R18). `method` is omitted when
 * the caller does not set one, so the host's `paste` default applies.
 */
export async function typeTool(
  host: HostRequester,
  args: TypeArgs,
): Promise<ToolResult<ActionResult>> {
  try {
    const payload = await host.request<unknown>(
      "action",
      { kind: "type", ...args },
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = ActionResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a type result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}
