import { readFileSync } from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

import {
  CONNECT_TIMEOUT_MS,
  defaultSessionDir,
  HELLO_TIMEOUT_MS,
  HOST_REQUEST_TIMEOUT_MS,
  MCP_TOOL_TIMEOUT_MS,
  SERVER_VERSION,
  SPAWN_WAIT_MS,
  STATUS_TIMEOUT_MS,
} from "../src/config.js";

describe("config", () => {
  it("single-sources the version from package.json", () => {
    const packageJson = JSON.parse(
      readFileSync(new URL("../package.json", import.meta.url), "utf8"),
    ) as { version: string };

    expect(SERVER_VERSION).toBe(packageJson.version);
  });

  it("keeps the timeout hierarchy monotonic", () => {
    expect(MCP_TOOL_TIMEOUT_MS).toBeGreaterThan(HOST_REQUEST_TIMEOUT_MS);
    expect(HOST_REQUEST_TIMEOUT_MS).toBeGreaterThanOrEqual(STATUS_TIMEOUT_MS);
    expect(STATUS_TIMEOUT_MS).toBeGreaterThan(HELLO_TIMEOUT_MS);
    expect(HELLO_TIMEOUT_MS).toBeGreaterThan(CONNECT_TIMEOUT_MS);
    expect(SPAWN_WAIT_MS).toBeGreaterThan(0);
  });

  it("honours the session directory override before platform defaults", () => {
    expect(defaultSessionDir({ env: { KENNECT_COMPUTER_SESSION_DIR: "C:\\custom" } })).toBe(
      path.resolve("C:\\custom"),
    );

    expect(defaultSessionDir({ platform: "win32", env: { LOCALAPPDATA: "C:\\Users\\test\\AppData\\Local" } })).toBe(
      path.join("C:\\Users\\test\\AppData\\Local", "KennectComputer"),
    );

    expect(defaultSessionDir({ platform: "darwin", env: {} })).toContain(
      "KennectComputer",
    );
  });
});
