import type { Snapshot, WindowInfo } from "@kennect/shared";

/** A selected Notepad window and a protocol-valid accessibility snapshot of it. */
export const notepadWindow: WindowInfo = {
  windowId: "w_1",
  title: "Untitled - Notepad",
  processName: "notepad",
  pid: 4242,
  bounds: { x: 0, y: 0, width: 800, height: 600 },
  focused: true,
  minimized: false,
  visible: true,
};

export const notepadSnapshot: Snapshot = {
  window: notepadWindow,
  generation: 3,
  focusedNodeId: "n3:2",
  modal: false,
  nodes: [
    {
      nodeId: "n3:1",
      role: "text",
      name: "File",
      bounds: { x: 0, y: 0, width: 100, height: 20 },
      enabled: true,
      focused: false,
      actions: [],
    },
    {
      nodeId: "n3:2",
      role: "edit",
      name: "Text editor",
      identifier: "editor",
      bounds: { x: 0, y: 20, width: 400, height: 200 },
      enabled: true,
      focused: true,
      actions: ["focus", "setValue"],
    },
  ],
  totalNodes: 2,
  truncated: false,
};
