import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Timeouts decrease inward (R24): the host request deadline is larger than the
 * handshake and connect deadlines. Keeping them in one module makes the
 * ordering testable.
 */
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
export const CONFIG_PATH_ENV = "KENNECT_COMPUTER_CONFIG";

/** DESIGN §14: seven days of metadata-only audit retention by default. */
export const AUDIT_RETENTION_DAYS = 7;

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

/**
 * Resolves the audit log directory: `<user data dir>/audit` on Windows and
 * macOS (DESIGN §14). It follows `KENNECT_COMPUTER_SESSION_DIR` so tests and
 * development runs stay inside their isolated data directory.
 */
export function defaultAuditDir(options: SessionDirOptions = {}): string {
  return path.join(defaultSessionDir(options), "audit");
}

export interface ConfigFileOptions extends SessionDirOptions {
  /** Replaces the default config path; defaults to `KENNECT_COMPUTER_CONFIG`. */
  configPath?: string;
  /** Replaces the per-user overlay path; `null` disables the overlay. */
  userConfigPath?: string | null;
}

/**
 * The config the host reads: `KENNECT_COMPUTER_CONFIG` when set, otherwise
 * `config/default.json` beside the checkout (both `src/` and `dist/` sit three
 * levels below the repository root).
 */
export function defaultConfigPath(env: NodeJS.ProcessEnv = process.env): string {
  const override = env[CONFIG_PATH_ENV];
  if (override !== undefined && override.length > 0) {
    return path.resolve(override);
  }

  return fileURLToPath(new URL("../../../config/default.json", import.meta.url));
}

/** The per-user overlay beside the session directory. */
export function defaultUserConfigPath(options: SessionDirOptions = {}): string {
  return path.join(defaultSessionDir(options), "config.json");
}

/**
 * Resolves `privacy.auditRetentionDays` from the default config plus the
 * per-user overlay. A missing, malformed, or invalid value falls back to the
 * design default: auditing config must never prevent the server from starting.
 */
export function resolveAuditRetentionDays(options: ConfigFileOptions = {}): number {
  const env = options.env ?? process.env;
  const configPath = options.configPath ?? defaultConfigPath(env);
  const userConfigPath = options.userConfigPath === undefined
    ? defaultUserConfigPath({ env, platform: options.platform, home: options.home })
    : options.userConfigPath;

  const merged = readConfigObject(configPath);
  if (userConfigPath !== null) {
    mergeConfigObjects(merged, readConfigObject(userConfigPath));
  }

  const privacy = merged.privacy;
  const value = isConfigObject(privacy) ? privacy.auditRetentionDays : undefined;

  return typeof value === "number" && Number.isFinite(value) && value >= 0
    ? Math.floor(value)
    : AUDIT_RETENTION_DAYS;
}

function readConfigObject(filePath: string): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(readFileSync(filePath, "utf8"));
    return isConfigObject(parsed) ? parsed : {};
  } catch {
    // Missing or malformed config is treated as empty; defaults apply.
    return {};
  }
}

/** Property-wise deep merge; objects merge, scalars and arrays replace. */
function mergeConfigObjects(
  target: Record<string, unknown>,
  overlay: Record<string, unknown>,
): void {
  for (const [key, value] of Object.entries(overlay)) {
    const existingKey = Object.keys(target)
      .find((candidate) => candidate.toLowerCase() === key.toLowerCase());
    const existing = existingKey === undefined ? undefined : target[existingKey];

    if (isConfigObject(value) && isConfigObject(existing)) {
      mergeConfigObjects(existing, value);
      continue;
    }

    target[existingKey ?? key] = value;
  }
}

function isConfigObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
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
