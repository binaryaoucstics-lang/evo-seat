import type { Tool } from "@earendil-works/pi-mcp";

const groups: Record<string, readonly string[]> = {
  common: ["seat_run", "seat_windows", "seat_observe", "seat_element", "seat_wait", "seat_screenshot", "seat_window", "seat_click", "seat_key", "seat_type"],
  input: ["seat_move", "seat_drag", "seat_scroll", "seat_clipboard_read", "seat_clipboard_write"],
  diagnostics: ["anode_guide", "seat_status", "seat_capabilities", "seat_lease", "seat_launch_status"],
  desktop: ["seat_browser", "seat_display", "seat_exec", "seat_job", "seat_processes", "seat_kill_process"],
  audio: ["seat_audio_status", "seat_audio_listen", "seat_audio_play", "seat_audio_stop"],
  android: ["android_status", "android_emulator", "android_studio"],
  steam: ["steam_status", "steam_launch"],
  gamepad: ["gamepad_attach", "gamepad_detach", "gamepad_set", "gamepad_tap", "gamepad_reset"],
};
const groupOf = new Map(Object.entries(groups).flatMap(([group, names]) => names.map(name => [name, group] as const)));
const audioSummary = "Capture seat audio metrics (duration, peak and silence); audio samples are omitted from model input.";
const examples: Record<string, Record<string, unknown>> = {
  seat_run: { path: "C:\\Apps\\App.exe", args: [] }, seat_windows: {},
  seat_observe: { windowId: "w_…" },
  seat_screenshot: {},
  seat_element: { snapshotId: "s_…", elementId: "e…", action: "invoke", observeAfter: true },
  seat_wait: { windowId: "w_…", name: "<exact transient label from the last snapshot>", state: "missing", waitMs: 5000 },
};
const brief = (tool: Tool) => ({ name: tool.name, description: tool.name === "seat_audio_listen" ? audioSummary : (tool.title ?? tool.description?.split(/[.\n]/, 1)[0] ?? tool.name)
  .replace(/^(?:EVO Seat|Anode):\s*/i, "") });

export function seatCatalog(tools: Tool[], name?: string, group?: string) {
  if (name !== undefined) {
    if (group !== undefined) throw new Error("Choose a tool name or a group.");
    const tool = tools.find(t => t.name === name);
    if (!tool) throw new Error("Unknown or unavailable EVO Seat tool: " + name);
    if (name === "seat_audio_listen") return { ...tool, description: audioSummary + " " + (tool.description ?? "") };
    if (name === "seat_element") return { ...tool, inputSchema: { ...tool.inputSchema, properties: {
      ...(tool.inputSchema.properties as Record<string, unknown>), observeAfter: { type: "boolean",
        description: "After a successful intermediate action, return a fresh text snapshot of its source window. The write is never replayed if the read fails. Verify final submissions separately." },
    } } };
    if (name !== "seat_run" && name !== "seat_lease") return tool;
    const properties = tool.inputSchema.properties as Record<string, unknown> | undefined;
    return { ...tool, inputSchema: { ...tool.inputSchema, properties: name === "seat_run" ? { ...properties,
      reuse: { type: "boolean", description: "Reuse a matching planned app by default; false explicitly requests a fresh launch." } }
      : { action: properties?.action } }, ...(name === "seat_lease" ? {
        description: "Check, acquire, renew or release this run's assigned lease. EVO manages its layout, timeout and release options." } : {}) };
  }
  const selected = tools.map(tool => ({ tool, group: groupOf.get(tool.name) ?? "other" }));
  const available = [...new Set(selected.map(item => item.group))];
  if (group === "all") return tools.map(brief);
  if (group !== undefined && !available.includes(group)) throw new Error("Unavailable tool group. Choose: " + [...available, "all"].join(", "));
  const entries = selected.filter(item => item.group === (group ?? "common")).map(({tool}) =>
    examples[tool.name] ? { ...brief(tool), example: examples[tool.name], ...(tool.name === "seat_element" ? {
      batch: { snapshotId: "s_…", action: "set_values", values: [{ elementId: "e…", value: "text" }], observeAfter: true },
    } : {}) } : brief(tool));
  return group === undefined ? { tools: entries, groups: available.filter(key => key !== "common"),
    ...(tools.some(t => t.name === "seat_lease") ? { lease: 'seat_lease {action:"acquire"}' } : {}) } : entries;
}
