import { z } from "zod";
import {
  ERROR_CODES,
  PROTOCOL_VERSION,
  type ErrorCode,
  type HelloParams,
  type HostStatus,
  type ImagePayload,
  type Node,
  type Rect,
  type Snapshot,
  type WindowInfo,
} from "./protocol.js";

/**
 * Runtime validation for protocol payloads.
 *
 * `WindowInfo`, `Node`, and `Snapshot` are strict: unknown fields are rejected
 * so native handles or other platform details cannot leak through the protocol.
 */

export const RectSchema = z.object({
  x: z.number().finite(),
  y: z.number().finite(),
  width: z.number().finite().nonnegative(),
  height: z.number().finite().nonnegative(),
});

export const WindowInfoSchema = z
  .object({
    windowId: z.string().min(1),
    title: z.string(),
    processName: z.string().min(1),
    pid: z.number().int().nonnegative(),
    bounds: RectSchema,
    focused: z.boolean(),
    minimized: z.boolean(),
    visible: z.boolean(),
  })
  .strict();

export const NodeSchema = z
  .object({
    nodeId: z.string().min(1),
    role: z.string().min(1),
    name: z.string(),
    identifier: z.string().optional(),
    bounds: RectSchema,
    enabled: z.boolean(),
    focused: z.boolean(),
    actions: z.array(z.string()),
  })
  .strict();

export const ImagePayloadSchema = z.object({
  data: z.string().min(1),
  mimeType: z.literal("image/png"),
  scale: z.number().finite().positive(),
  sourceBounds: RectSchema,
});

export const SnapshotSchema = z
  .object({
    window: WindowInfoSchema,
    generation: z.number().int().nonnegative(),
    focusedNodeId: z.string().min(1).optional(),
    modal: z.boolean(),
    nodes: z.array(NodeSchema),
    totalNodes: z.number().int().nonnegative(),
    truncated: z.boolean(),
    image: ImagePayloadSchema.optional(),
    warning: z.string().optional(),
  })
  .strict();

export const MonitorInfoSchema = z
  .object({
    id: z.string().min(1),
    name: z.string(),
    bounds: RectSchema,
    scale: z.number().finite().positive(),
    primary: z.boolean(),
  })
  .strict();

export const PermissionsSchema = z
  .object({
    accessibility: z.boolean(),
    screenRecording: z.boolean(),
  })
  .strict();

export const HostStatusSchema = z
  .object({
    hostVersion: z.string().min(1),
    protocolVersion: z.literal(PROTOCOL_VERSION),
    platform: z.enum(["windows", "macos"]),
    locked: z.boolean(),
    monitors: z.array(MonitorInfoSchema),
    permissions: PermissionsSchema,
  })
  .strict();

/** A 32-byte base64url token has 43 characters and no padding. */
export const TOKEN_PATTERN = /^[A-Za-z0-9_-]{43}$/;

export const HelloParamsSchema = z.object({
  token: z.string().regex(TOKEN_PATTERN, "expected a 32-byte base64url token"),
  protocolVersion: z.literal(PROTOCOL_VERSION),
  clientName: z.string().min(1),
});

export const ErrorCodeSchema = z.enum(ERROR_CODES);

// Compile-time checks that the schemas and the hand-written interfaces agree.
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2
  ? true
  : false;
type Assert<T extends true> = T;

type _RectMatches = Assert<Equal<z.infer<typeof RectSchema>, Rect>>;
type _WindowInfoMatches = Assert<Equal<z.infer<typeof WindowInfoSchema>, WindowInfo>>;
type _NodeMatches = Assert<Equal<z.infer<typeof NodeSchema>, Node>>;
type _ImagePayloadMatches = Assert<Equal<z.infer<typeof ImagePayloadSchema>, ImagePayload>>;
type _SnapshotMatches = Assert<Equal<z.infer<typeof SnapshotSchema>, Snapshot>>;
type _HostStatusMatches = Assert<Equal<z.infer<typeof HostStatusSchema>, HostStatus>>;
type _HelloParamsMatches = Assert<Equal<z.infer<typeof HelloParamsSchema>, HelloParams>>;
type _ErrorCodeMatches = Assert<Equal<z.infer<typeof ErrorCodeSchema>, ErrorCode>>;
