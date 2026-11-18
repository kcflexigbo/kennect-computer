import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import {
  AUDIT_RETENTION_DAYS,
  CONNECT_TIMEOUT_MS,
  defaultAuditDir,
  defaultConfigPath,
  defaultSessionDir,
  HELLO_TIMEOUT_MS,
  HOST_REQUEST_TIMEOUT_MS,
  resolveAuditRetentionDays,
  SERVER_VERSION,
  SPAWN_WAIT_MS,
  STATUS_TIMEOUT_MS,
} from "../src/config.js";
import { DEFAULT_SNAPSHOT_MAX_DEPTH } from "../src/tools/snapshot.js";

const temporaryDirectories: string[] = [];

function temporaryDirectory(): string {
  const dir = mkdtempSync(path.join(tmpdir(), "kennect-config-test-"));
  temporaryDirectories.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of temporaryDirectories.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

describe("config", () => {
  it("single-sources the version from package.json", () => {
    const packageJson = JSON.parse(
      readFileSync(new URL("../package.json", import.meta.url), "utf8"),
    ) as { version: string };

    expect(SERVER_VERSION).toBe(packageJson.version);
  });

  it("keeps the timeout hierarchy monotonic", () => {
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

  it("places the audit directory under the user data directory", () => {
    expect(defaultAuditDir({ env: { KENNECT_COMPUTER_SESSION_DIR: "C:\\custom" } })).toBe(
      path.resolve("C:\\custom", "audit"),
    );

    expect(defaultAuditDir({ platform: "win32", env: { LOCALAPPDATA: "C:\\Users\\test\\AppData\\Local" } })).toBe(
      path.join("C:\\Users\\test\\AppData\\Local", "KennectComputer", "audit"),
    );

    expect(defaultAuditDir({ platform: "darwin", env: {}, home: "/Users/test" })).toBe(
      path.join("/Users/test", "Library", "Application Support", "KennectComputer", "audit"),
    );
  });

  it("retains seven days of audit entries by default", () => {
    expect(AUDIT_RETENTION_DAYS).toBe(7);
  });

  it("honours the config path environment override", () => {
    expect(
      defaultConfigPath({ KENNECT_COMPUTER_CONFIG: "C:\\custom\\config.json" }),
    ).toBe(path.resolve("C:\\custom\\config.json"));
  });
});

describe("audit retention config", () => {
  it("reads privacy.auditRetentionDays with the per-user overlay applied", () => {
    const dir = temporaryDirectory();
    const defaults = path.join(dir, "default.json");
    const user = path.join(dir, "user.json");
    writeFileSync(
      defaults,
      JSON.stringify({
        privacy: { auditRetentionDays: 7, excludedTitles: ["*bank*"] },
      }),
      "utf8",
    );
    writeFileSync(user, JSON.stringify({ privacy: { auditRetentionDays: 3 } }), "utf8");

    expect(
      resolveAuditRetentionDays({
        configPath: defaults,
        userConfigPath: user,
      }),
    ).toBe(3);
    expect(
      resolveAuditRetentionDays({
        configPath: defaults,
        userConfigPath: null,
      }),
    ).toBe(7);
  });

  it("falls back to the design default for missing or malformed config", () => {
    const dir = temporaryDirectory();
    const malformed = path.join(dir, "malformed.json");
    const wrongType = path.join(dir, "wrong-type.json");
    writeFileSync(malformed, "{ not json", "utf8");
    writeFileSync(
      wrongType,
      JSON.stringify({ privacy: { auditRetentionDays: "seven" } }),
      "utf8",
    );

    expect(
      resolveAuditRetentionDays({
        configPath: path.join(dir, "missing.json"),
        userConfigPath: null,
      }),
    ).toBe(AUDIT_RETENTION_DAYS);
    expect(
      resolveAuditRetentionDays({ configPath: malformed, userConfigPath: null }),
    ).toBe(AUDIT_RETENTION_DAYS);
    expect(
      resolveAuditRetentionDays({ configPath: wrongType, userConfigPath: null }),
    ).toBe(AUDIT_RETENTION_DAYS);
  });

  it("keeps config/default.json in sync with the code defaults", () => {
    // The same numbers exist in config/default.json, AppConfig (C#), and the
    // MCP defaults; the host-side ConfigTests pin the C# side.
    const repoConfig = JSON.parse(
      readFileSync(new URL("../../../config/default.json", import.meta.url), "utf8"),
    ) as {
      perception: { maxDepth: number };
      privacy: { auditRetentionDays: number };
    };

    expect(repoConfig.perception.maxDepth).toBe(DEFAULT_SNAPSHOT_MAX_DEPTH);
    expect(repoConfig.privacy.auditRetentionDays).toBe(AUDIT_RETENTION_DAYS);
  });
});
