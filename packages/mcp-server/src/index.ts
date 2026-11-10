import { pathToFileURL } from "node:url";

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";

import { SERVER_NAME, SERVER_VERSION } from "./config.js";
import { HostManager } from "./host-manager.js";
import { registerComputerTools, SERVER_INSTRUCTIONS, type HostProvider } from "./tools.js";

/**
 * Builds the MCP server without connecting a transport, so tests can drive it
 * over an in-memory transport.
 */
export function createMcpServer(provider: HostProvider = new HostManager()): Server {
  const server = new Server(
    { name: SERVER_NAME, version: SERVER_VERSION },
    {
      capabilities: { tools: {} },
      instructions: SERVER_INSTRUCTIONS,
    },
  );

  registerComputerTools(server, provider);
  return server;
}

async function main(): Promise<void> {
  const manager = new HostManager();
  const server = createMcpServer(manager);
  const transport = new StdioServerTransport();

  const shutdown = (): void => {
    manager.close();
    void server.close();
  };

  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);

  await server.connect(transport);
}

function isMainModule(): boolean {
  const entry = process.argv[1];
  if (entry === undefined || entry.length === 0) {
    return false;
  }

  try {
    return import.meta.url === pathToFileURL(entry).href;
  } catch {
    return false;
  }
}

if (isMainModule()) {
  main().catch((error: unknown) => {
    // stdio transport owns stdout; diagnostics go to stderr only.
    console.error("kennect-computer MCP server failed:", error);
    process.exitCode = 1;
  });
}
