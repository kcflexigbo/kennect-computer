import { describe, expect, it, vi } from "vitest";

import { STATUS_TIMEOUT_MS } from "../src/config.js";
import { HostError } from "../src/errors.js";
import { statusTool, type HostRequester } from "../src/tools/status.js";
import { readStatusFixture } from "./support/fixture.js";

function requester(request: HostRequester["request"]): HostRequester {
  return { request };
}

describe("statusTool", () => {
  it("returns the typed envelope with HostStatus data", async () => {
    const fixture = readStatusFixture();
    const request = vi.fn().mockResolvedValue(fixture);

    const result = await statusTool(requester(request));

    expect(result).toEqual({ ok: true, data: fixture });
    expect(result.ok).toBe(true);
    expect(result.data).toEqual(fixture);
    expect(request).toHaveBeenCalledTimes(1);
    expect(request).toHaveBeenCalledWith("status", {}, STATUS_TIMEOUT_MS);
  });

  it("maps ACTION_TIMEOUT to an isError envelope that is retryable", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("ACTION_TIMEOUT", "The host did not answer; it was not retried."),
    );

    const result = await statusTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "ACTION_TIMEOUT",
      message: "The host did not answer; it was not retried.",
      retryable: true,
    });
  });

  it("maps HOST_UNAVAILABLE to a retryable isError envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("HOST_UNAVAILABLE", "The host is not running."),
    );

    const result = await statusTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "The host is not running.",
      retryable: true,
    });
  });

  it("maps UNAUTHORIZED to a non-retryable isError envelope", async () => {
    const request = vi.fn().mockRejectedValue(
      new HostError("UNAUTHORIZED", "The session token was rejected."),
    );

    const result = await statusTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "UNAUTHORIZED",
      message: "The session token was rejected.",
      retryable: false,
    });
  });

  it("rejects a payload that does not match the protocol schema", async () => {
    const request = vi.fn().mockResolvedValue({ hostVersion: "0.1.0", protocolVersion: 1 });

    const result = await statusTool(requester(request));

    expect(result.ok).toBe(false);
    expect(result.code).toBe("HOST_UNAVAILABLE");
    expect(result.retryable).toBe(true);
    expect(result.message).toContain("status");
  });

  it("maps an unexpected failure to HOST_UNAVAILABLE", async () => {
    const request = vi.fn().mockRejectedValue(new Error("socket exploded"));

    const result = await statusTool(requester(request));

    expect(result).toEqual({
      ok: false,
      code: "HOST_UNAVAILABLE",
      message: "socket exploded",
      retryable: true,
    });
  });
});
