/**
 * Platform-neutral protocol shared by the MCP server and every host.
 *
 * Window IDs are opaque strings and native handles never appear in protocol
 * payloads. Node IDs are ephemeral and valid only for the snapshot generation
 * that produced them.
 */

/** Version of the JSON-RPC protocol spoken between the MCP server and a host. */
export const PROTOCOL_VERSION = 1;

export type ProtocolVersion = typeof PROTOCOL_VERSION;

export type Platform = "windows" | "macos";

/** A rectangle in physical screen pixels. */
export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** A top-level window as reported to the client. */
export interface WindowInfo {
  /** Opaque identifier; native handles never cross the protocol boundary. */
  windowId: string;
  /** Window title. Metadata only: the title may change without invalidating a selection. */
  title: string;
  processName: string;
  pid: number;
  bounds: Rect;
  focused: boolean;
  minimized: boolean;
  visible: boolean;
}

/** A single accessibility node in a snapshot. */
export interface Node {
  /** Ephemeral ID, `n<generation>:<index>`, valid only for the producing snapshot. */
  nodeId: string;
  role: string;
  name: string;
  /** Platform accessibility identifier, when the app exposes one. */
  identifier?: string;
  bounds: Rect;
  enabled: boolean;
  focused: boolean;
  /** Generic action names supported by the node, e.g. `press` or `focus`. */
  actions: string[];
}

/** A downscaled PNG captured alongside a snapshot. */
export interface ImagePayload {
  /** Base64-encoded PNG bytes. */
  data: string;
  mimeType: "image/png";
  /** Physical pixels per returned image pixel. */
  scale: number;
  sourceBounds: Rect;
}

/** The result of `snapshot`: a window identity, its accessibility outline, and optionally an image. */
export interface Snapshot {
  window: WindowInfo;
  /** Increments with every snapshot; node IDs embed this generation. */
  generation: number;
  focusedNodeId?: string;
  modal: boolean;
  nodes: Node[];
  totalNodes: number;
  truncated: boolean;
  image?: ImagePayload;
  /** Present when perception degraded, e.g. an empty accessibility tree with vision fallback. */
  warning?: string;
}

/** A display known to the host. */
export interface MonitorInfo {
  id: string;
  name: string;
  bounds: Rect;
  scale: number;
  primary: boolean;
}

/** OS-level permissions the host needs in order to capture and control the desktop. */
export interface Permissions {
  accessibility: boolean;
  screenRecording: boolean;
}

/** Host health and environment, returned by `status`. */
export interface HostStatus {
  hostVersion: string;
  protocolVersion: ProtocolVersion;
  platform: Platform;
  locked: boolean;
  monitors: MonitorInfo[];
  permissions: Permissions;
}

/** Parameters of the `hello` handshake. The token is 32 random bytes, base64url-encoded. */
export interface HelloParams {
  token: string;
  protocolVersion: ProtocolVersion;
  clientName: string;
}

/** Result of a successful `hello`. */
export interface HelloResult {
  hostVersion: string;
  protocolVersion: ProtocolVersion;
  platform: Platform;
  pid: number;
}

/** Machine-readable error codes carried by every failed tool or host call. */
export const ERROR_CODES = [
  "STALE_SNAPSHOT",
  "WINDOW_GONE",
  "FOCUS_REFUSED",
  "DESKTOP_UNAVAILABLE",
  "PERMISSION_MISSING",
  "HOST_UNAVAILABLE",
  "APP_NOT_RESPONDING",
  "EXPECTATION_FAILED",
  "LOCKED_BY_OTHER_SESSION",
  "AUTHORIZATION_DENIED",
  "INVALID_REQUEST",
  "UNAUTHORIZED",
  "METHOD_NOT_FOUND",
  "ACTION_TIMEOUT",
  "FRAME_TOO_LARGE",
] as const;

export type ErrorCode = (typeof ERROR_CODES)[number];
