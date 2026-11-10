import type { WindowInfo } from "@kennect/shared";
import { WindowInfoSchema } from "@kennect/shared/schemas";
import { z } from "zod";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { HostError } from "../errors.js";
import { failure, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

/** A window offered when a process/title target matches more than one window (R20). */
export const WindowCandidateSchema = z
  .object({
    windowId: z.string().min(1),
    title: z.string(),
    pid: z.number().int().nonnegative(),
  })
  .strict();

export type WindowCandidate = z.infer<typeof WindowCandidateSchema>;

const WindowCandidatesSchema = z.array(WindowCandidateSchema);

/** The `window.select` payload: `{ window }` (R18). */
export const WindowSelectResultSchema = z
  .object({
    window: WindowInfoSchema,
  })
  .strict();

export interface SelectArgs {
  windowId?: string;
  process?: string;
  title?: string;
}

export interface SelectedWindowData {
  window: WindowInfo;
}

export interface AmbiguousTargetData {
  candidates: WindowCandidate[];
}

export type SelectData = SelectedWindowData | AmbiguousTargetData;

/**
 * `computer_select`: asks the host to select and focus a window and returns
 * `{ ok: true, data: { window } }` (R18). Target resolution is fail-closed in
 * the host (R20); an ambiguous process/title target returns `INVALID_REQUEST`
 * with the candidate list under `data.candidates` instead of picking one.
 */
export async function selectTool(
  host: HostRequester,
  args: SelectArgs,
): Promise<ToolResult<SelectData>> {
  try {
    const payload = await host.request<unknown>(
      "window.select",
      args,
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = WindowSelectResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a select result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return ok({ window: parsed.data.window });
  } catch (error) {
    if (error instanceof HostError) {
      const candidates = error.code === "INVALID_REQUEST"
        ? readCandidates(error.data)
        : undefined;

      if (candidates !== undefined) {
        return {
          ok: false,
          code: error.code,
          message: error.message,
          retryable: error.retryable,
          data: { candidates },
        };
      }

      const retryable = error.code === "HOST_UNAVAILABLE" || error.code === "ACTION_TIMEOUT";
      return failure(error.code, error.message, { retryable });
    }

    return failure(
      "HOST_UNAVAILABLE",
      error instanceof Error && error.message.length > 0
        ? error.message
        : "The select request failed.",
      { retryable: true },
    );
  }
}

/** Reads the host's structured candidates, ignoring a malformed list. */
function readCandidates(data: unknown): WindowCandidate[] | undefined {
  if (data === null || typeof data !== "object") {
    return undefined;
  }

  const parsed = WindowCandidatesSchema.safeParse(
    (data as { candidates?: unknown }).candidates,
  );

  return parsed.success && parsed.data.length > 0 ? parsed.data : undefined;
}
