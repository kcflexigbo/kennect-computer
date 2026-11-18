import { z } from "zod";
import type {
  ActionResult,
  ActionStep,
  ActionStepKind,
  BatchRequest,
  BatchResult,
  BatchStepResult,
  ClickRequest,
  CoordinateSpace,
  ExpectPredicate,
  FindRequest,
  FindResult,
  KeyRequest,
  MouseButton,
  ReadTextRequest,
  ReadTextResult,
  ScrollDirection,
  ScrollRequest,
  TypeMethod,
  TypeRequest,
  WaitCondition,
  WaitRequest,
  WaitResult,
} from "./actions.js";
import { ErrorCodeSchema, NodeSchema, SnapshotSchema, WindowInfoSchema } from "./schemas.js";

/**
 * Runtime validation for M2 action requests and results.
 *
 * Objects are strict so platform details cannot leak through the protocol.
 * Defaults (`space: "image"`, `button: "left"`, `method: "paste"`, wait bounds,
 * batch caps) belong to the host; the schemas only validate explicit values.
 */

export const CoordinateSpaceSchema = z.enum(["image", "screen"]);
export const MouseButtonSchema = z.enum(["left", "middle", "right"]);
export const ScrollDirectionSchema = z.enum(["up", "down", "left", "right"]);
export const TypeMethodSchema = z.enum(["paste", "value", "keys"]);
export const ActionStepKindSchema = z.enum(["click", "type", "key", "scroll", "wait", "readText"]);

/**
 * Documented maxima shared by the schemas, the MCP tool descriptions, and the
 * host's own parsers. They keep a malformed request from making the host
 * allocate without bound (a huge click count) or type long after the client's
 * deadline (a huge keys text).
 */
export const MAX_CLICK_COUNT = 10;
export const MAX_TYPE_TEXT_LENGTH = 100_000;
export const MAX_WAIT_TIMEOUT_MS = 60_000;
export const MAX_FIND_RESULTS = 500;
export const MAX_READ_TEXT_LENGTH = 50_000;

const nodeId = z.string().min(1);
const finiteNumber = z.number().finite();
const selectorFields = {
  role: z.string().min(1).optional(),
  name: z.string().min(1).optional(),
  identifier: z.string().min(1).optional(),
};

interface SelectorFields {
  role?: string;
  name?: string;
  identifier?: string;
}

interface TargetFields {
  nodeId?: string;
  x?: number;
  y?: number;
}

function hasSelector(value: SelectorFields): boolean {
  return value.role !== undefined || value.name !== undefined || value.identifier !== undefined;
}

/** Click and scroll target exactly one of a node ID or an (x, y) pair. */
function hasSingleTarget(value: TargetFields): boolean {
  const hasNode = value.nodeId !== undefined;
  const hasX = value.x !== undefined;
  const hasY = value.y !== undefined;
  return hasNode ? !hasX && !hasY : hasX && hasY;
}

const TARGET_MESSAGE = "provide exactly one of nodeId or x and y";

export const ExpectPredicateSchema = z
  .discriminatedUnion("kind", [
    z.object({ kind: z.literal("foreground") }).strict(),
    z
      .object({
        kind: z.literal("node"),
        ...selectorFields,
        focused: z.boolean().optional(),
      })
      .strict(),
    z
      .object({
        kind: z.literal("text"),
        ...selectorFields,
        contains: z.string().min(1),
      })
      .strict(),
  ])
  .superRefine((predicate, ctx) => {
    if (predicate.kind === "node" && !hasSelector(predicate)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "a node predicate requires role, name, or identifier",
      });
    }
  });

const clickRequestShape = {
  nodeId: nodeId.optional(),
  x: finiteNumber.optional(),
  y: finiteNumber.optional(),
  space: CoordinateSpaceSchema.optional(),
  button: MouseButtonSchema.optional(),
  count: z.number().int().positive().max(MAX_CLICK_COUNT).optional(),
  modifiers: z.array(z.string().min(1)).max(4).optional(),
  expect: z.array(ExpectPredicateSchema).optional(),
};

export const ClickRequestSchema = z
  .object(clickRequestShape)
  .strict()
  .refine(hasSingleTarget, { message: TARGET_MESSAGE });

const typeRequestShape = {
  text: z.string().min(1).max(MAX_TYPE_TEXT_LENGTH),
  method: TypeMethodSchema.optional(),
  nodeId: nodeId.optional(),
  expect: z.array(ExpectPredicateSchema).optional(),
};

export const TypeRequestSchema = z.object(typeRequestShape).strict();

const keyRequestShape = {
  keys: z.string().min(1),
  expect: z.array(ExpectPredicateSchema).optional(),
};

export const KeyRequestSchema = z.object(keyRequestShape).strict();

const scrollRequestShape = {
  nodeId: nodeId.optional(),
  x: finiteNumber.optional(),
  y: finiteNumber.optional(),
  space: CoordinateSpaceSchema.optional(),
  direction: ScrollDirectionSchema,
  amount: z.number().int().positive().optional(),
  expect: z.array(ExpectPredicateSchema).optional(),
};

export const ScrollRequestSchema = z
  .object(scrollRequestShape)
  .strict()
  .refine(hasSingleTarget, { message: TARGET_MESSAGE });

export const FindRequestSchema = z
  .object({
    text: z.string().min(1).optional(),
    role: z.string().min(1).optional(),
    maxResults: z.number().int().positive().max(MAX_FIND_RESULTS).optional(),
  })
  .strict();

export const WaitConditionSchema = z
  .discriminatedUnion("kind", [
    z
      .object({
        kind: z.literal("window"),
        process: z.string().min(1).optional(),
        title: z.string().min(1).optional(),
      })
      .strict(),
    z
      .object({
        kind: z.literal("node"),
        ...selectorFields,
      })
      .strict(),
    z
      .object({
        kind: z.literal("text"),
        ...selectorFields,
        contains: z.string().min(1),
      })
      .strict(),
  ])
  .superRefine((condition, ctx) => {
    if (condition.kind === "window" && condition.process === undefined && condition.title === undefined) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "a window condition requires process or title",
      });
    }
    if (condition.kind === "node" && !hasSelector(condition)) {
      ctx.addIssue({
        code: z.ZodIssueCode.custom,
        message: "a node condition requires role, name, or identifier",
      });
    }
  });

const waitRequestShape = {
  condition: WaitConditionSchema,
  present: z.boolean().optional(),
  timeoutMs: z.number().int().positive().max(MAX_WAIT_TIMEOUT_MS).optional(),
  pollMs: z.number().int().positive().optional(),
};

export const WaitRequestSchema = z.object(waitRequestShape).strict();

const readTextRequestShape = {
  nodeId,
  maxLength: z.number().int().positive().max(MAX_READ_TEXT_LENGTH).optional(),
};

export const ReadTextRequestSchema = z.object(readTextRequestShape).strict();

/**
 * A batch step is a request flattened together with its `kind` discriminator.
 * The click and scroll target refinement is re-applied at the union level
 * because a discriminated-union member must remain a plain object schema.
 */
export const ActionStepSchema = z
  .discriminatedUnion("kind", [
    z.object({ kind: z.literal("click"), ...clickRequestShape }).strict(),
    z.object({ kind: z.literal("type"), ...typeRequestShape }).strict(),
    z.object({ kind: z.literal("key"), ...keyRequestShape }).strict(),
    z.object({ kind: z.literal("scroll"), ...scrollRequestShape }).strict(),
    z.object({ kind: z.literal("wait"), ...waitRequestShape }).strict(),
    z.object({ kind: z.literal("readText"), ...readTextRequestShape }).strict(),
  ])
  .superRefine((step, ctx) => {
    if ((step.kind === "click" || step.kind === "scroll") && !hasSingleTarget(step)) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, message: TARGET_MESSAGE });
    }
  });

export const BatchRequestSchema = z
  .object({
    steps: z.array(ActionStepSchema).min(1).max(50),
    snapshotBefore: z.boolean().optional(),
    snapshotAfter: z.boolean().optional(),
  })
  .strict();

export const BatchStepResultSchema = z
  .object({
    index: z.number().int().nonnegative(),
    kind: ActionStepKindSchema,
    ok: z.boolean(),
    code: ErrorCodeSchema.optional(),
    message: z.string().optional(),
    data: z.unknown().optional(),
  })
  .strict();

export const ActionResultSchema = z
  .object({
    window: WindowInfoSchema,
    action: z.string().min(1),
    method: z.string().min(1),
    needsObservation: z.boolean(),
  })
  .strict();

export const FindResultSchema = z
  .object({
    window: WindowInfoSchema,
    generation: z.number().int().nonnegative(),
    matches: z.array(NodeSchema),
    scanned: z.number().int().nonnegative(),
    truncated: z.boolean(),
  })
  .strict();

export const ReadTextResultSchema = z
  .object({
    window: WindowInfoSchema,
    nodeId,
    text: z.string(),
    length: z.number().int().nonnegative(),
    truncated: z.boolean(),
  })
  .strict();

export const WaitResultSchema = z
  .object({
    window: WindowInfoSchema.optional(),
    condition: WaitConditionSchema,
    elapsedMs: z.number().finite().nonnegative(),
  })
  .strict();

export const BatchResultSchema = z
  .object({
    steps: z.array(BatchStepResultSchema),
    failedIndex: z.number().int().nonnegative().optional(),
    snapshotBefore: SnapshotSchema.optional(),
    snapshotAfter: SnapshotSchema.optional(),
    warning: z.string().optional(),
  })
  .strict();

// Compile-time checks that the schemas and the hand-written interfaces agree.
type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2
  ? true
  : false;
type Extends<A, B> = A extends B ? true : false;
// `ActionStep` is an intersection of a kind object and a request interface;
// TypeScript does not consider that identical to zod's flattened output, so
// those two shapes are checked for mutual assignability instead.
type MutuallyAssignable<A, B> = Extends<A, B> extends true ? Extends<B, A> : false;
type Assert<T extends true> = T;

type _CoordinateSpaceMatches = Assert<Equal<z.infer<typeof CoordinateSpaceSchema>, CoordinateSpace>>;
type _MouseButtonMatches = Assert<Equal<z.infer<typeof MouseButtonSchema>, MouseButton>>;
type _ScrollDirectionMatches = Assert<Equal<z.infer<typeof ScrollDirectionSchema>, ScrollDirection>>;
type _TypeMethodMatches = Assert<Equal<z.infer<typeof TypeMethodSchema>, TypeMethod>>;
type _ActionStepKindMatches = Assert<Equal<z.infer<typeof ActionStepKindSchema>, ActionStepKind>>;
type _ExpectPredicateMatches = Assert<Equal<z.infer<typeof ExpectPredicateSchema>, ExpectPredicate>>;
type _ClickRequestMatches = Assert<Equal<z.infer<typeof ClickRequestSchema>, ClickRequest>>;
type _TypeRequestMatches = Assert<Equal<z.infer<typeof TypeRequestSchema>, TypeRequest>>;
type _KeyRequestMatches = Assert<Equal<z.infer<typeof KeyRequestSchema>, KeyRequest>>;
type _ScrollRequestMatches = Assert<Equal<z.infer<typeof ScrollRequestSchema>, ScrollRequest>>;
type _FindRequestMatches = Assert<Equal<z.infer<typeof FindRequestSchema>, FindRequest>>;
type _WaitConditionMatches = Assert<Equal<z.infer<typeof WaitConditionSchema>, WaitCondition>>;
type _WaitRequestMatches = Assert<Equal<z.infer<typeof WaitRequestSchema>, WaitRequest>>;
type _ReadTextRequestMatches = Assert<Equal<z.infer<typeof ReadTextRequestSchema>, ReadTextRequest>>;
type _ActionStepMatches = Assert<MutuallyAssignable<z.infer<typeof ActionStepSchema>, ActionStep>>;
type _BatchRequestMatches = Assert<MutuallyAssignable<z.infer<typeof BatchRequestSchema>, BatchRequest>>;
type _ActionResultMatches = Assert<Equal<z.infer<typeof ActionResultSchema>, ActionResult>>;
type _FindResultMatches = Assert<Equal<z.infer<typeof FindResultSchema>, FindResult>>;
type _ReadTextResultMatches = Assert<Equal<z.infer<typeof ReadTextResultSchema>, ReadTextResult>>;
type _WaitResultMatches = Assert<Equal<z.infer<typeof WaitResultSchema>, WaitResult>>;
type _BatchStepResultMatches = Assert<Equal<z.infer<typeof BatchStepResultSchema>, BatchStepResult>>;
type _BatchResultMatches = Assert<Equal<z.infer<typeof BatchResultSchema>, BatchResult>>;
