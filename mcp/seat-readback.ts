import type { CallToolResult } from "@earendil-works/pi-mcp";

// Only bind references that the native executor returned as structured data.
// A title, an application's text or another visible window is never a fallback.
export class SeatReadback {
  private readonly windows = new Map<string, string>();

  remember(windowId: unknown, result: CallToolResult) {
    const data = result.structuredContent as Record<string, unknown> | undefined;
    if (result.isError || typeof windowId !== "string" || !windowId || data?.windowId !== windowId ||
        typeof data.snapshotId !== "string" || !data.snapshotId || !Array.isArray(data.elements)) return;
    this.windows.set(data.snapshotId, windowId);
    if (this.windows.size > 16) this.windows.delete(this.windows.keys().next().value!);
    return data.snapshotId;
  }

  clear() { this.windows.clear(); }

  async element(input: Record<string, unknown>,
    act: (args: Record<string, unknown>) => Promise<CallToolResult>,
    observe: (windowId: string) => Promise<CallToolResult>): Promise<CallToolResult> {
    const { observeAfter, ...args } = input;
    if (observeAfter !== undefined && typeof observeAfter !== "boolean") {
      return { isError: true, structuredContent: { started: false, errorCode: "invalid_arguments" },
        content: [{ type: "text", text: "observeAfter must be true or false. No action was sent." }] };
    }
    const windowId = typeof args.snapshotId === "string" ? this.windows.get(args.snapshotId) : undefined;
    this.clear(); // Native element actions consume their snapshot, even on uncertain outcomes.
    const ack = await act(args);
    if (!observeAfter || ack.isError) return ack;
    const original = ack.structuredContent && typeof ack.structuredContent === "object" ? ack.structuredContent : {};
    const unavailable = (reason: string): CallToolResult => ({
      ...ack, structuredContent: { ...original, readback: { status: "unavailable", reason } },
      content: [...ack.content, { type: "text", text: "Action acknowledged; readback unavailable (" + reason +
        "). Do not repeat the action. Use seat_windows/seat_observe or seat_wait to check the outcome." }],
    });
    if (!windowId) return unavailable("unbound_snapshot");
    try {
      const view = await observe(windowId);
      if (view.isError) return unavailable("observation_failed");
      const data = view.structuredContent as { window?: { enabled?: boolean }; snapshotId?: string } | undefined;
      if (data?.window?.enabled === false) return unavailable("source_window_disabled");
      if (data?.snapshotId === args.snapshotId || !this.remember(windowId, view)) return unavailable("no_fresh_snapshot");
      return { ...ack, structuredContent: { ...original, readback: { status: "observed", observation: view.structuredContent } },
        content: [...ack.content, { type: "text", text: "Fresh snapshot of the source window immediately after the action (not a completion check):" },
          ...view.content] };
    } catch { return unavailable("observation_interrupted"); }
  }
}
