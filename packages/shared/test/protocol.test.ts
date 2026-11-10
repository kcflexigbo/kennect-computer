import { describe, it, expect } from "vitest";
import { HelloParamsSchema, NodeSchema, SnapshotSchema, WindowInfoSchema } from "../src/schemas";
import snapshotFixture from "../fixtures/snapshot.json";

describe("protocol schemas", () => {
  it("accepts a 32-byte base64url token and rejects a missing one", () => {
    expect(() => HelloParamsSchema.parse({ token: "a".repeat(43), protocolVersion: 1, clientName: "claude" })).not.toThrow();
    expect(() => HelloParamsSchema.parse({ protocolVersion: 1, clientName: "claude" })).toThrow();
  });
  it("rejects a snapshot node without a nodeId", () => {
    const bad = { ...snapshotFixture, nodes: [{ role: "button", name: "Send", bounds: { x: 0, y: 0, width: 10, height: 10 }, enabled: true, focused: false, actions: ["press"] }] };
    expect(() => SnapshotSchema.parse(bad)).toThrow();
  });
  it("rejects a native handle instead of exposing it", () => {
    expect(() => WindowInfoSchema.parse({ ...(snapshotFixture as any).window, hwnd: 1234 })).toThrow();
  });
  it("requires enabled, focused, and actions on every node", () => {
    expect(() => NodeSchema.parse({ nodeId: "n1:1", role: "button", name: "Send", bounds: { x: 0, y: 0, width: 1, height: 1 } })).toThrow();
  });
});
