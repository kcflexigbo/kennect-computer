import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { Node, Snapshot } from "@kennect/shared";
import { SnapshotSchema } from "@kennect/shared/schemas";

import { HOST_REQUEST_TIMEOUT_MS } from "../config.js";
import { HostError } from "../errors.js";
import { failure, ok, type ToolResult } from "../tool-result.js";
import type { HostRequester } from "./status.js";

export const SNAPSHOT_MODES = ["a11y", "vision", "both"] as const;

export type SnapshotMode = (typeof SNAPSHOT_MODES)[number];

/** Mirrors the host's generic `SnapshotOptions.MaxDepth` default. */
export const DEFAULT_SNAPSHOT_MAX_DEPTH = 12;

/** A crop rectangle in the previous snapshot image's pixel space. */
export interface SnapshotRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface SnapshotArgs {
  windowId: string;
  mode?: SnapshotMode;
  maxDepth?: number;
  maxNodes?: number;
  image?: boolean;
  region?: SnapshotRegion;
}

export interface SnapshotData {
  snapshot: Snapshot;
}

/**
 * The tool result plus the MCP content blocks: the human-readable outline as
 * text, followed by the PNG as an image block when the host returned one.
 * Failures carry the JSON envelope as text instead.
 */
export interface SnapshotToolOutput {
  result: ToolResult<SnapshotData>;
  text: string;
  content: CallToolResult["content"];
}

/**
 * `computer_snapshot`: captures a window's accessibility outline through the
 * host `snapshot` method and returns the R18 envelope with the snapshot plus an
 * outline for the model. When `image` was requested and the host captured one,
 * a downscaled PNG is returned as an MCP image content block.
 */
export async function snapshotTool(
  host: HostRequester,
  args: SnapshotArgs,
): Promise<SnapshotToolOutput> {
  const params: Record<string, unknown> = { windowId: args.windowId };

  if (args.mode !== undefined) {
    params.mode = args.mode;
  }
  if (args.maxDepth !== undefined) {
    params.maxDepth = args.maxDepth;
  }
  if (args.maxNodes !== undefined) {
    params.maxNodes = args.maxNodes;
  }
  if (args.image !== undefined) {
    params.image = args.image;
  }
  if (args.region !== undefined) {
    params.region = args.region;
  }

  try {
    const payload = await host.request<unknown>(
      "snapshot",
      params,
      HOST_REQUEST_TIMEOUT_MS,
    );
    const parsed = SnapshotSchema.safeParse(payload);

    if (!parsed.success) {
      return failureOutput(
        failure(
          "HOST_UNAVAILABLE",
          "The host returned a snapshot that does not match protocol v1.",
          { retryable: true },
        ),
      );
    }

    const text = formatSnapshotOutline(parsed.data, args.maxDepth);
    const content: CallToolResult["content"] = [{ type: "text", text }];

    if (parsed.data.image !== undefined) {
      content.push({
        type: "image",
        data: parsed.data.image.data,
        mimeType: parsed.data.image.mimeType,
      });
    }

    return {
      result: ok({ snapshot: parsed.data }),
      text,
      content,
    };
  } catch (error) {
    return failureOutput(failureFromSnapshotError(error));
  }
}

function failureOutput(result: ToolResult<never>): SnapshotToolOutput {
  const text = JSON.stringify(result);
  return { result, text, content: [{ type: "text", text }] };
}

function failureFromSnapshotError(error: unknown): ToolResult<never> {
  if (error instanceof HostError) {
    const retryable = error.code === "HOST_UNAVAILABLE" || error.code === "ACTION_TIMEOUT";
    return failure(error.code, error.message, { retryable });
  }

  return failure(
    "HOST_UNAVAILABLE",
    error instanceof Error && error.message.length > 0
      ? error.message
      : "The snapshot request failed.",
    { retryable: true },
  );
}

/**
 * The DESIGN §5 outline: a compact state header followed by one line per node.
 * All names and titles are sanitized because they come from the target app and
 * are untrusted data.
 */
export function formatSnapshotOutline(
  snapshot: Snapshot,
  maxDepth: number = DEFAULT_SNAPSHOT_MAX_DEPTH,
): string {
  const { window } = snapshot;
  const lines = [
    `app: ${sanitizeText(window.processName)} (pid ${window.pid})`,
    `window: "${sanitizeText(window.title)}" (id ${window.windowId}, `
      + `${window.bounds.width}x${window.bounds.height} `
      + `at ${window.bounds.x},${window.bounds.y})`,
    `focused: ${describeFocusedNode(snapshot)}`,
    `modal: ${snapshot.modal ? "yes" : "none"}`,
    `nodes: ${snapshot.nodes.length} shown / ${snapshot.totalNodes} total `
      + `(depth ${maxDepth}${snapshot.truncated ? ", truncated" : ""})`,
  ];

  for (const node of snapshot.nodes) {
    lines.push(formatNode(node));
  }

  return lines.join("\n");
}

function describeFocusedNode(snapshot: Snapshot): string {
  if (snapshot.focusedNodeId === undefined) {
    return "none";
  }

  const focused = snapshot.nodes.find((node) => node.nodeId === snapshot.focusedNodeId);
  return focused === undefined
    ? "none"
    : `[${focused.nodeId}] ${focused.role} "${sanitizeText(focused.name)}"`;
}

function formatNode(node: Node): string {
  const parts = [`[${node.nodeId}]`, node.role, `"${sanitizeText(node.name)}"`];

  if (node.identifier !== undefined && node.identifier.length > 0) {
    parts.push(`id=${sanitizeText(node.identifier)}`);
  }

  parts.push(
    `@ ${node.bounds.x},${node.bounds.y} ${node.bounds.width}x${node.bounds.height}`,
  );

  if (node.actions.length > 0) {
    parts.push(`actions=${node.actions.join(",")}`);
  }

  if (node.focused) {
    parts.push("focused");
  }

  return parts.join(" ");
}

/** Collapses control characters, quoted text, and whitespace to one safe line. */
function sanitizeText(value: string): string {
  return value
    .replace(/[\u0000-\u001f\u007f]+/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/"/g, "'");
}
