import { z } from "zod";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { HostError } from "../errors.js";
import { failure, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

/** The host's `stop` result: `{ ok: true }`. */
export const StopResultSchema = z.object({ ok: z.literal(true) }).strict();

export type StopResult = z.infer<typeof StopResultSchema>;

/**
 * `computer_stop`: asks the host to abort the in-flight operation. The host
 * cancels the current operation's token and stays alive, so stopping with
 * nothing running is still a success (R35). Returns `{ ok: true, data: { ok: true } }`
 * under the R18 envelope.
 */
export async function stopTool(
  host: HostRequester,
): Promise<ToolResult<StopResult>> {
  try {
    const payload = await host.request<unknown>("stop", {}, HOST_REQUEST_TIMEOUT_MS);
    const parsed = StopResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a stop result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok({ ok: true });
  } catch (error) {
    if (error instanceof HostError) {
      const retryable = error.code === "HOST_UNAVAILABLE" || error.code === "ACTION_TIMEOUT";
      return failure(error.code, error.message, { retryable });
    }

    return failure(
      "HOST_UNAVAILABLE",
      error instanceof Error && error.message.length > 0
        ? error.message
        : "The stop request failed.",
      { retryable: true },
    );
  }
}
