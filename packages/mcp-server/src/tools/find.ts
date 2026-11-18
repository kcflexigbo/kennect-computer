import type { FindRequest, FindResult } from "@kennect/shared/actions";
import { FindResultSchema } from "@kennect/shared/action-schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type FindArgs = FindRequest;

/**
 * `computer_find`: sends a search through the host's `find` method and returns
 * `{ ok: true, data: FindResult }` (R18). A fresh perception walk assigns the
 * generation that the returned node IDs belong to; no matches is a successful
 * empty result, not an error.
 */
export async function findTool(
  host: HostRequester,
  args: FindArgs,
): Promise<ToolResult<FindResult>> {
  try {
    const payload = await host.request<unknown>(
      "find",
      args,
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = FindResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a find result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}
