import { describe, it, expect } from "vitest";
import {
  ActionResultSchema,
  ActionStepSchema,
  BatchRequestSchema,
  BatchResultSchema,
  ClickRequestSchema,
  ExpectPredicateSchema,
  FindRequestSchema,
  FindResultSchema,
  KeyRequestSchema,
  ReadTextRequestSchema,
  ReadTextResultSchema,
  ScrollRequestSchema,
  TypeRequestSchema,
  WaitRequestSchema,
  WaitResultSchema,
} from "../src/action-schemas";
import snapshotFixture from "../fixtures/snapshot.json";

const windowInfo = snapshotFixture.window;

const windowCondition = { kind: "window", process: "Feishu" } as const;

describe("ClickRequestSchema", () => {
  it("accepts a node target or a coordinate pair", () => {
    expect(ClickRequestSchema.parse({ nodeId: "n1:2" })).toMatchObject({ nodeId: "n1:2" });
    expect(ClickRequestSchema.parse({ x: 10, y: 20 })).toMatchObject({ x: 10, y: 20 });
  });

  it("accepts optional fields and both coordinate spaces", () => {
    const parsed = ClickRequestSchema.parse({
      x: 10,
      y: 20,
      space: "screen",
      button: "right",
      count: 2,
      expect: [{ kind: "foreground" }],
    });
    expect(parsed.space).toBe("screen");
    expect(parsed.button).toBe("right");
    expect(ClickRequestSchema.parse({ nodeId: "n1:2", space: "image" }).space).toBe("image");
  });

  it("rejects a request with no target or a partial coordinate pair", () => {
    expect(() => ClickRequestSchema.parse({})).toThrow();
    expect(() => ClickRequestSchema.parse({ x: 10 })).toThrow();
    expect(() => ClickRequestSchema.parse({ y: 20 })).toThrow();
  });

  it("rejects a node target combined with coordinates", () => {
    expect(() => ClickRequestSchema.parse({ nodeId: "n1:2", x: 10, y: 20 })).toThrow();
  });

  it("rejects unknown fields", () => {
    expect(() => ClickRequestSchema.parse({ nodeId: "n1:2", hwnd: 1234 })).toThrow();
  });

  it("accepts only image or screen as the coordinate space", () => {
    expect(() => ClickRequestSchema.parse({ nodeId: "n1:2", space: "physical" })).toThrow();
  });

  it("bounds count to 1..10", () => {
    expect(ClickRequestSchema.parse({ nodeId: "n1:2", count: 10 }).count).toBe(10);
    expect(() => ClickRequestSchema.parse({ nodeId: "n1:2", count: 0 })).toThrow();
    expect(() => ClickRequestSchema.parse({ nodeId: "n1:2", count: 11 })).toThrow();
  });

  it("accepts click modifiers and rejects empty or excessive lists", () => {
    expect(
      ClickRequestSchema.parse({ nodeId: "n1:2", modifiers: ["Ctrl", "Shift"] }).modifiers,
    ).toEqual(["Ctrl", "Shift"]);
    expect(() => ClickRequestSchema.parse({ nodeId: "n1:2", modifiers: [""] })).toThrow();
    expect(() =>
      ClickRequestSchema.parse({ nodeId: "n1:2", modifiers: ["Ctrl", "Shift", "Alt", "Win", "Ctrl"] }),
    ).toThrow();
  });
});

describe("TypeRequestSchema", () => {
  it("requires non-empty text", () => {
    expect(TypeRequestSchema.parse({ text: "hi" }).text).toBe("hi");
    expect(() => TypeRequestSchema.parse({})).toThrow();
    expect(() => TypeRequestSchema.parse({ text: "" })).toThrow();
  });

  it("accepts only paste, value, or keys as the method", () => {
    expect(TypeRequestSchema.parse({ text: "hi", method: "paste" }).method).toBe("paste");
    expect(TypeRequestSchema.parse({ text: "hi", method: "value" }).method).toBe("value");
    expect(TypeRequestSchema.parse({ text: "hi", method: "keys" }).method).toBe("keys");
    expect(() => TypeRequestSchema.parse({ text: "hi", method: "clipboard" })).toThrow();
  });

  it("accepts a node target and expectations", () => {
    const parsed = TypeRequestSchema.parse({
      text: "hi",
      nodeId: "n1:2",
      expect: [{ kind: "node", role: "edit", focused: true }],
    });
    expect(parsed.nodeId).toBe("n1:2");
  });

  it("bounds text to 100,000 characters", () => {
    expect(TypeRequestSchema.parse({ text: "a".repeat(100_000) }).text).toHaveLength(100_000);
    expect(() => TypeRequestSchema.parse({ text: "a".repeat(100_001) })).toThrow();
  });
});

describe("KeyRequestSchema", () => {
  it("requires a non-empty key or chord", () => {
    expect(KeyRequestSchema.parse({ keys: "Ctrl+A" }).keys).toBe("Ctrl+A");
    expect(() => KeyRequestSchema.parse({})).toThrow();
    expect(() => KeyRequestSchema.parse({ keys: "" })).toThrow();
  });

  it("rejects unknown fields", () => {
    expect(() => KeyRequestSchema.parse({ keys: "Enter", text: "hi" })).toThrow();
  });
});

describe("ScrollRequestSchema", () => {
  it("accepts a node or coordinate target with a direction", () => {
    expect(ScrollRequestSchema.parse({ nodeId: "n1:2", direction: "down" }).direction).toBe("down");
    const parsed = ScrollRequestSchema.parse({ x: 1, y: 2, space: "screen", direction: "left", amount: 2 });
    expect(parsed.amount).toBe(2);
  });

  it("requires exactly one target and a known direction", () => {
    expect(() => ScrollRequestSchema.parse({ direction: "down" })).toThrow();
    expect(() => ScrollRequestSchema.parse({ nodeId: "n1:2" })).toThrow();
    expect(() => ScrollRequestSchema.parse({ nodeId: "n1:2", x: 1, y: 2, direction: "down" })).toThrow();
    expect(() => ScrollRequestSchema.parse({ nodeId: "n1:2", direction: "diagonal" })).toThrow();
  });
});

describe("WaitRequestSchema", () => {
  it("accepts a window condition and a text condition", () => {
    const byWindow = WaitRequestSchema.parse({ condition: windowCondition, present: true, timeoutMs: 5000, pollMs: 50 });
    expect(byWindow.condition).toMatchObject(windowCondition);
    const byText = WaitRequestSchema.parse({ condition: { kind: "text", contains: "Ready" } });
    expect(byText.condition).toMatchObject({ kind: "text", contains: "Ready" });
  });

  it("accepts a node condition", () => {
    const parsed = WaitRequestSchema.parse({ condition: { kind: "node", role: "button", name: "Send" } });
    expect(parsed.condition).toMatchObject({ kind: "node", name: "Send" });
  });

  it("rejects a missing or empty condition", () => {
    expect(() => WaitRequestSchema.parse({})).toThrow();
    expect(() => WaitRequestSchema.parse({ condition: {} })).toThrow();
    expect(() => WaitRequestSchema.parse({ condition: { kind: "window" } })).toThrow();
    expect(() => WaitRequestSchema.parse({ condition: { kind: "node" } })).toThrow();
  });

  it("rejects unknown fields", () => {
    expect(() => WaitRequestSchema.parse({ condition: windowCondition, interval: 5 })).toThrow();
  });

  it("bounds timeoutMs to 1..60,000", () => {
    expect(WaitRequestSchema.parse({ condition: windowCondition, timeoutMs: 60_000 }).timeoutMs)
      .toBe(60_000);
    expect(() => WaitRequestSchema.parse({ condition: windowCondition, timeoutMs: 60_001 })).toThrow();
  });
});

describe("FindRequestSchema", () => {
  it("accepts text, role, and a result cap", () => {
    expect(FindRequestSchema.parse({ text: "send", maxResults: 10 }).maxResults).toBe(10);
    expect(FindRequestSchema.parse({ role: "button" }).role).toBe("button");
  });

  it("rejects invalid caps and unknown fields", () => {
    expect(() => FindRequestSchema.parse({ maxResults: 0 })).toThrow();
    expect(() => FindRequestSchema.parse({ text: "send", depth: 3 })).toThrow();
  });

  it("bounds maxResults to 1..500", () => {
    expect(FindRequestSchema.parse({ maxResults: 500 }).maxResults).toBe(500);
    expect(() => FindRequestSchema.parse({ maxResults: 501 })).toThrow();
  });
});

describe("ReadTextRequestSchema", () => {
  it("requires a nodeId", () => {
    expect(ReadTextRequestSchema.parse({ nodeId: "n1:2" }).nodeId).toBe("n1:2");
    expect(() => ReadTextRequestSchema.parse({})).toThrow();
    expect(() => ReadTextRequestSchema.parse({ nodeId: "" })).toThrow();
  });

  it("accepts and validates maxLength", () => {
    expect(ReadTextRequestSchema.parse({ nodeId: "n1:2", maxLength: 1000 }).maxLength).toBe(1000);
    expect(() => ReadTextRequestSchema.parse({ nodeId: "n1:2", maxLength: 0 })).toThrow();
  });

  it("bounds maxLength to 1..50,000", () => {
    expect(ReadTextRequestSchema.parse({ nodeId: "n1:2", maxLength: 50_000 }).maxLength)
      .toBe(50_000);
    expect(() => ReadTextRequestSchema.parse({ nodeId: "n1:2", maxLength: 50_001 })).toThrow();
  });
});

describe("ExpectPredicateSchema", () => {
  it("accepts all three predicate kinds", () => {
    expect(ExpectPredicateSchema.parse({ kind: "foreground" })).toEqual({ kind: "foreground" });
    expect(ExpectPredicateSchema.parse({ kind: "node", role: "button", name: "Send", identifier: "send-button", focused: true })).toMatchObject({ kind: "node" });
    expect(ExpectPredicateSchema.parse({ kind: "text", role: "edit", contains: "Draft" })).toMatchObject({ kind: "text", contains: "Draft" });
  });

  it("rejects a text predicate without contains", () => {
    expect(() => ExpectPredicateSchema.parse({ kind: "text" })).toThrow();
    expect(() => ExpectPredicateSchema.parse({ kind: "text", contains: "" })).toThrow();
  });

  it("rejects a node predicate without a selector", () => {
    expect(() => ExpectPredicateSchema.parse({ kind: "node" })).toThrow();
    expect(() => ExpectPredicateSchema.parse({ kind: "node", focused: true })).toThrow();
  });
});

describe("ActionStepSchema", () => {
  it("parses a mixed step array in order", () => {
    const parsed = ActionStepSchema.array().parse([
      { kind: "click", nodeId: "n1:2" },
      { kind: "type", text: "hi" },
      { kind: "wait", condition: windowCondition },
    ]);
    expect(parsed.map((step) => step.kind)).toEqual(["click", "type", "wait"]);
  });

  it("parses key, scroll, and readText steps", () => {
    const parsed = ActionStepSchema.array().parse([
      { kind: "key", keys: "Enter" },
      { kind: "scroll", nodeId: "n1:2", direction: "down", amount: 3 },
      { kind: "readText", nodeId: "n1:2", maxLength: 500 },
    ]);
    expect(parsed.map((step) => step.kind)).toEqual(["key", "scroll", "readText"]);
  });

  it("rejects an unknown kind", () => {
    expect(() => ActionStepSchema.parse({ kind: "drag", nodeId: "n1:2" })).toThrow();
  });

  it("applies the target refinement to click and scroll steps", () => {
    expect(() => ActionStepSchema.parse({ kind: "click" })).toThrow();
    expect(() => ActionStepSchema.parse({ kind: "click", x: 10 })).toThrow();
    expect(() => ActionStepSchema.parse({ kind: "scroll", nodeId: "n1:2" })).toThrow();
  });

  it("rejects unknown fields on a step", () => {
    expect(() => ActionStepSchema.parse({ kind: "type", text: "hi", hwnd: 1 })).toThrow();
  });
});

describe("BatchRequestSchema", () => {
  const step = { kind: "wait", condition: windowCondition };

  it("accepts 1 to 50 steps and snapshot flags", () => {
    const parsed = BatchRequestSchema.parse({ steps: [step], snapshotBefore: true, snapshotAfter: false });
    expect(parsed.steps).toHaveLength(1);
    expect(BatchRequestSchema.parse({ steps: Array.from({ length: 50 }, () => step) }).steps).toHaveLength(50);
  });

  it("rejects an empty steps array and more than 50 steps", () => {
    expect(() => BatchRequestSchema.parse({ steps: [] })).toThrow();
    expect(() => BatchRequestSchema.parse({ steps: Array.from({ length: 51 }, () => step) })).toThrow();
  });

  it("rejects non-boolean snapshot flags", () => {
    expect(() => BatchRequestSchema.parse({ steps: [step], snapshotBefore: "yes" })).toThrow();
  });
});

describe("result schemas", () => {
  it("validates an ActionResult", () => {
    const parsed = ActionResultSchema.parse({
      window: windowInfo,
      action: "click",
      method: "invoke",
      needsObservation: true,
    });
    expect(parsed.needsObservation).toBe(true);
    expect(() => ActionResultSchema.parse({ window: windowInfo, action: "click", method: "invoke" })).toThrow();
    expect(() => ActionResultSchema.parse({ window: windowInfo, action: "click", method: "invoke", needsObservation: true, hwnd: 1 })).toThrow();
  });

  it("validates a FindResult", () => {
    const parsed = FindResultSchema.parse({
      window: windowInfo,
      generation: 2,
      matches: [snapshotFixture.nodes[1]],
      scanned: 12,
      truncated: false,
    });
    expect(parsed.matches).toHaveLength(1);
    expect(parsed.generation).toBe(2);
  });

  it("validates a ReadTextResult", () => {
    const text = "Kennect 汉字 123";
    const parsed = ReadTextResultSchema.parse({
      window: windowInfo,
      nodeId: "n1:2",
      text,
      length: text.length,
      truncated: false,
    });
    expect(parsed.text).toBe(text);
    expect(parsed.length).toBe(text.length);
  });

  it("validates a WaitResult with and without a matched window", () => {
    const withWindow = WaitResultSchema.parse({ window: windowInfo, condition: windowCondition, elapsedMs: 120 });
    expect(withWindow.window?.windowId).toBe("w_1");
    expect(WaitResultSchema.parse({ condition: { kind: "text", contains: "Ready" }, elapsedMs: 0 }).window).toBeUndefined();
  });

  it("validates a BatchResult with ordered steps, a failed index, and a warning", () => {
    const parsed = BatchResultSchema.parse({
      steps: [
        { index: 0, kind: "click", ok: true, data: { window: windowInfo, action: "click", method: "invoke", needsObservation: true } },
        { index: 1, kind: "type", ok: false, code: "EXPECTATION_FAILED", message: "expect text:Draft failed" },
      ],
      failedIndex: 1,
      snapshotBefore: snapshotFixture,
      snapshotAfter: snapshotFixture,
      warning: "snapshotAfter failed: WINDOW_GONE",
    });
    expect(parsed.steps.map((step) => step.index)).toEqual([0, 1]);
    expect(parsed.failedIndex).toBe(1);
    expect(parsed.warning).toContain("snapshotAfter");
  });

  it("rejects an unknown step kind or error code in a BatchResult", () => {
    expect(() => BatchResultSchema.parse({ steps: [{ index: 0, kind: "drag", ok: true }] })).toThrow();
    expect(() => BatchResultSchema.parse({ steps: [{ index: 0, kind: "click", ok: false, code: "NOT_A_CODE" }] })).toThrow();
  });
});
