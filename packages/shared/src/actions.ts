/**
 * Platform-neutral action protocol shared by the MCP server and every host.
 *
 * Action requests reference either an ephemeral snapshot node ID (preferred)
 * or explicit coordinates in the requested coordinate space. Results carry the
 * window they acted on; node IDs and native handles never cross the protocol
 * boundary.
 */

import type { ErrorCode, Node, Snapshot, WindowInfo } from "./protocol.js";

/** Pixel space used by coordinate actions. Defaults to `image` on the host. */
export type CoordinateSpace = "image" | "screen";

/** Mouse button used by click actions. Defaults to `left` on the host. */
export type MouseButton = "left" | "middle" | "right";

export type ScrollDirection = "up" | "down" | "left" | "right";

/** How typed text reaches the control. Defaults to `paste` on the host. */
export type TypeMethod = "paste" | "value" | "keys";

/** A point in the coordinate space named by the accompanying request. */
export interface Point {
  x: number;
  y: number;
}

/**
 * A precondition evaluated against a fresh accessibility read immediately
 * before input. The first failing predicate returns `EXPECTATION_FAILED` and
 * no input is sent.
 */
export type ExpectPredicate =
  | { kind: "foreground" }
  | { kind: "node"; role?: string; name?: string; identifier?: string; focused?: boolean }
  | { kind: "text"; role?: string; name?: string; identifier?: string; contains: string };

/** Click a node or a point; exactly one of `nodeId` or (`x` and `y`) is required. */
export interface ClickRequest {
  nodeId?: string;
  x?: number;
  y?: number;
  space?: CoordinateSpace;
  button?: MouseButton;
  count?: number;
  /** Modifier names held around the click, e.g. `["Ctrl", "Shift"]`. */
  modifiers?: string[];
  expect?: ExpectPredicate[];
}

/** Type text through paste (default), a value pattern, or synthesized keys. */
export interface TypeRequest {
  text: string;
  method?: TypeMethod;
  nodeId?: string;
  expect?: ExpectPredicate[];
}

/** Press a single key or chord, e.g. `Enter`, `Tab`, or `Ctrl+Shift+S`. */
export interface KeyRequest {
  keys: string;
  expect?: ExpectPredicate[];
}

/** Scroll at a node or point; exactly one target is required. */
export interface ScrollRequest {
  nodeId?: string;
  x?: number;
  y?: number;
  space?: CoordinateSpace;
  direction: ScrollDirection;
  amount?: number;
  expect?: ExpectPredicate[];
}

/** Search the selected window's accessibility tree by text and/or role. */
export interface FindRequest {
  text?: string;
  role?: string;
  maxResults?: number;
}

/**
 * A wait condition: a window exists, a node exists, or a node's text contains
 * a string. `present: false` inverts the condition.
 */
export type WaitCondition =
  | { kind: "window"; process?: string; title?: string }
  | { kind: "node"; role?: string; name?: string; identifier?: string }
  | { kind: "text"; role?: string; name?: string; identifier?: string; contains: string };

/** Wait defaults on the host: `present: true`, 10,000 ms timeout, 100 ms poll. */
export interface WaitRequest {
  condition: WaitCondition;
  present?: boolean;
  timeoutMs?: number;
  pollMs?: number;
}

/** Read text through TextPattern or ValuePattern, capped by `maxLength`. */
export interface ReadTextRequest {
  nodeId: string;
  maxLength?: number;
}

/** Result of a mutating action. `action` is the request kind; `method` is how it was carried out. */
export interface ActionResult {
  window: WindowInfo;
  action: string;
  method: string;
  needsObservation: boolean;
}

/** Result of `find`; `matches` carry node IDs valid for `generation`. */
export interface FindResult {
  window: WindowInfo;
  generation: number;
  matches: Node[];
  scanned: number;
  truncated: boolean;
}

/** Result of `read_text`; `length` is the length of the returned text. */
export interface ReadTextResult {
  window: WindowInfo;
  nodeId: string;
  text: string;
  length: number;
  truncated: boolean;
}

/** Result of `wait`; `window` is present when a window condition matched. */
export interface WaitResult {
  window?: WindowInfo;
  condition: WaitCondition;
  elapsedMs: number;
}

/** Kinds of steps allowed in an ordered batch. */
export type ActionStepKind = "click" | "type" | "key" | "scroll" | "wait" | "readText";

/** A batch step is a request plus its `kind` discriminator. */
export type ActionStep =
  | ({ kind: "click" } & ClickRequest)
  | ({ kind: "type" } & TypeRequest)
  | ({ kind: "key" } & KeyRequest)
  | ({ kind: "scroll" } & ScrollRequest)
  | ({ kind: "wait" } & WaitRequest)
  | ({ kind: "readText" } & ReadTextRequest);

/** An ordered batch of at most 50 steps, executed serially by the host. */
export interface BatchRequest {
  steps: ActionStep[];
  snapshotBefore?: boolean;
  snapshotAfter?: boolean;
}

/** Outcome of one batch step. `data` is the step's result payload, when any. */
export interface BatchStepResult {
  index: number;
  kind: ActionStepKind;
  ok: boolean;
  code?: ErrorCode;
  message?: string;
  data?: unknown;
}

/**
 * Result of a batch. Steps stop at the first failure; `failedIndex` is the
 * zero-based index of that step. Optional accessibility-only snapshots are
 * best-effort and may be accompanied by a `warning`.
 */
export interface BatchResult {
  steps: BatchStepResult[];
  failedIndex?: number;
  snapshotBefore?: Snapshot;
  snapshotAfter?: Snapshot;
  warning?: string;
}
