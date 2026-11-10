import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import type { HostStatus } from "@kennect/shared";

const fixturePath = fileURLToPath(
  new URL("../../../shared/fixtures/status.json", import.meta.url),
);

/** Reads the shared status fixture that the Windows host's conformance test also uses. */
export function readStatusFixture(): HostStatus {
  return JSON.parse(readFileSync(fixturePath, "utf8")) as HostStatus;
}

export const STATUS_FIXTURE_PATH = fixturePath;
