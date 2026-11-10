import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";

/**
 * Timeouts decrease inward (R24): the outer MCP call deadline is larger than
 * the host request deadline, which is larger than the handshake and connect
 * deadlines. Keeping them in one module makes the ordering testable.
 */
export const MCP_TOOL_TIMEOUT_MS = 15_000;
export const HOST_REQUEST_TIMEOUT_MS = 10_000;
export const STATUS_TIMEOUT_MS = 8_000;
export const HELLO_TIMEOUT_MS = 5_000;
export const CONNECT_TIMEOUT_MS = 3_000;

/** How long HostManager waits for `session.json`/`session.token` after a spawn. */
export const SPAWN_WAIT_MS = 2_000;

export const SERVER_NAME = "kennect-computer";
export const CLIENT_NAME = "kennect-computer-mcp";

export const SESSION_FILE_NAME = "session.json";
export const TOKEN_FILE_NAME = "session.token";

export const HOST_PATH_ENV = "KENNECT_HOST_PATH";
export const SESSION_DIR_ENV = "KENNECT_COMPUTER_SESSION_DIR";

/** The version is single-sourced from this package's `package.json`. */
export const SERVER_VERSION = readServerVersion();

export interface SessionDirOptions {
  platform?: NodeJS.Platform;
  env?: NodeJS.ProcessEnv;
  home?: string;
}

/** Resolves the host's discovery directory, honouring the test/dev override. */
export function defaultSessionDir(options: SessionDirOptions = {}): string {
  const platform = options.platform ?? process.platform;
  const env = options.env ?? process.env;
  const home = options.home ?? homedir();

  const override = env[SESSION_DIR_ENV];
  if (override) {
    return path.resolve(override);
  }

  if (platform === "win32") {
    const localAppData = env.LOCALAPPDATA ?? path.join(home, "AppData", "Local");
    return path.join(localAppData, "KennectComputer");
  }

  return path.join(home, "Library", "Application Support", "KennectComputer");
}

function readServerVersion(): string {
  try {
    const packageJson = JSON.parse(
      readFileSync(new URL("../package.json", import.meta.url), "utf8"),
    ) as { version?: unknown };

    return typeof packageJson.version === "string" && packageJson.version.length > 0
      ? packageJson.version
      : "0.0.0";
  } catch {
    return "0.0.0";
  }
}
