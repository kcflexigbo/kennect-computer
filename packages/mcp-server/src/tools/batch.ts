import type { BatchRequest, BatchResult } from "@kennect/shared/actions";
import { BatchResultSchema } from "@kennect/shared/action-schemas";

import {
  effectiveWaitTimeoutMs,
  HOST_REQUEST_GRACE_MS,
  HOST_REQUEST_TIMEOUT_MS,
} from "../config.js";
import { failure, failureFromError, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export type BatchArgs = BatchRequest;

/**
 * `computer_batch`: sends an ordered action list through the host's
 * `action.batch` method. When every step runs, the result is
 * `{ ok: true, data: BatchResult }` (R18), including any accessibility-only
 * before/after snapshots. A step failure yields an `ok: false` envelope
 * carrying that step's code and message plus the partial per-step results and
 * the failed index, because replaying the batch would repeat the steps that
 * already ran.
 */
export async function batchTool(
  host: HostRequester,
  args: BatchArgs,
): Promise<ToolResult<BatchResult>> {
  try {
    const payload = await host.request<unknown>(
      "action.batch",
      args,
      batchRequestTimeoutMs(args),
    );
    const parsed = BatchResultSchema.safeParse(payload);

    if (!parsed.success) {
      return failure(
        "HOST_UNAVAILABLE",
        "The host returned a batch result that does not match protocol v1.",
        { retryable: true },
      );
    }

    return batchEnvelope(parsed.data);
  } catch (error) {
    return failureFromError(error);
  }
}

/**
 * The host-request deadline for a batch: every step gets the budget of a
 * standalone host request, each wait step adds its own (capped) deadline, and
 * one grace margin covers framing and the final write. The host runs the steps
 * serially in one request, so the sum bounds the response time; a shorter
 * client deadline would reject the pending request while the host kept
 * mutating the desktop. The schema caps a batch at 50 steps, which bounds this
 * deadline.
 */
function batchRequestTimeoutMs(args: BatchArgs): number {
  const waitBudgetMs = args.steps.reduce(
    (total, step) =>
      step.kind === "wait"
        ? total + effectiveWaitTimeoutMs(step.timeoutMs)
        : total,
    0,
  );

  return Math.max(
    HOST_REQUEST_TIMEOUT_MS,
    HOST_REQUEST_TIMEOUT_MS * args.steps.length + waitBudgetMs + HOST_REQUEST_GRACE_MS,
  );
}

/**
 * Maps a completed host batch onto the R18 envelope. A host batch that stopped
 * between steps has a failed index but no record for that step: the step never
 * ran, so the stop's ACTION_TIMEOUT mapping applies. The failure is never
 * retryable as a whole because earlier steps already mutated the desktop.
 */
function batchEnvelope(result: BatchResult): ToolResult<BatchResult> {
  if (result.failedIndex === undefined) {
    return ok(result);
  }

  const failedStep = result.steps.find((step) => step.index === result.failedIndex);
  const stopped = failedStep === undefined;
  const code = stopped ? "ACTION_TIMEOUT" : failedStep.code ?? "HOST_UNAVAILABLE";
  const message = stopped
    ? "The operation was stopped."
    : failedStep.message ?? "The batch step failed.";

  return failure(code, message, {
    retryable: false,
    partial: {
      completed: result.steps.filter((step) => step.ok).length,
      failedIndex: result.failedIndex,
      results: result.steps,
    },
  });
}
