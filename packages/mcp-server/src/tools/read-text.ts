import type { ReadTextRequest, ReadTextResult } from "@kennect/shared/actions";
import { ReadTextResultSchema } from "@kennect/shared/action-schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type ReadTextArgs = ReadTextRequest;

/**
 * `computer_read_text`: reads a control's text through the host's `read_text`
 * method and returns `{ ok: true, data: ReadTextResult }` (R18). The text is
 * untrusted target content and is returned only to the caller; the audit
 * choke point records the target and outcome, never the text.
 */
export async function readTextTool(
  host: HostRequester,
  args: ReadTextArgs,
): Promise<ToolResult<ReadTextResult>> {
  try {
    const payload = await host.request<unknown>(
      "read_text",
      args,
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = ReadTextResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a read text result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}
