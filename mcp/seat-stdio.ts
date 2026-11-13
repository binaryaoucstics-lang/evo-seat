import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import {
  JSON_RPC_ERROR_CODES, McpClient, McpError, StdioTransport, SUPPORTED_PROTOCOL_VERSIONS, parseJsonRpcMessage,
  type JsonRpcId, type JsonRpcRequest,
} from "@earendil-works/pi-mcp";
import { seatCatalog } from "./seat-catalog.ts";
import { seatModelArguments } from "./seat-images.ts";
import { SeatReadback } from "./seat-readback.ts";

const desktopToolTimeout = (name: string, args: Record<string, unknown>) =>
  name === "seat_lease" && args.action === "release" ? 6_000 :
    name === "seat_lease" || ["steam_launch", "seat_display", "android_emulator"].includes(name) ? 215_000 : 65_000;

const executable = process.env.EVO_SEAT_EXECUTABLE ??
  fileURLToPath(new URL("../native/evo-seat.exe", import.meta.url));
const channel = process.env.EVO_SEAT_CHANNEL ?? "proto-dual";
const native = new McpClient({ name: "Codex", version: "1" });
const transport = new StdioTransport({
  command: executable, args: ["--channel", channel, "mcp"],
  cwd: process.cwd(), env: { ANODE_EPHEMERAL: "1", ANODE_MCP_DEFAULT_LAYERED: "1" }, stderr: "inherit",
});
const readback = new SeatReadback();
const pending = new Map<JsonRpcId, AbortController>();
const rpc = JSON_RPC_ERROR_CODES;

function send(payload: object) {
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", ...payload }) + "\n");
}

async function handle(request: JsonRpcRequest, signal: AbortSignal) {
  switch (request.method) {
    case "initialize": {
      const result = await native.connect(transport);
      const requested = (request.params as { protocolVersion?: string } | undefined)?.protocolVersion;
      return {
        ...result,
        protocolVersion: SUPPORTED_PROTOCOL_VERSIONS.find(version => version === requested) ?? result.protocolVersion,
        serverInfo: {
          ...result.serverInfo,
          websiteUrl: "https://github.com/binaryaoucstics-lang/evo-seat",
        },
        instructions: [
          "In Codex, use a background region by default. Omit layerCount so the layout grows as tasks join. Only use mode=foreground when the user explicitly asks for the main Windows desktop. Tools that require the whole background desktop need an explicit mode=exclusive lease, which blocks other background tasks until release.",
          result.instructions?.replace("Default mode=exclusive preserves the original shared desktop lease and first-come waiting. ", ""),
          "Use seat_element with observeAfter:true to get a fresh text snapshot after intermediate actions. Verify final submissions separately.",
          "When a transient loading label is visible, use seat_wait for that exact name with state:missing and waitMs:5000. Inspect the result, including when matched is false.",
          "Use seat_screenshot with format:png and no maxWidth when a decision depends on small text. For receipts, seat_observe with includeScreenshot:true returns controls and an image.",
        ].filter(Boolean).join("\n"),
      };
    }
    case "ping":
      return {};
    case "prompts/list":
    case "prompts/get":
      return native.request(request.method, request.params as Record<string, unknown> | undefined, { signal });
    case "tools/list": {
      const tools = await native.listTools({ signal });
      return { tools: tools.map(tool => {
        if (tool.name === "seat_element") return seatCatalog(tools, tool.name);
        if (tool.name !== "seat_lease") return tool;
        const properties = tool.inputSchema.properties as Record<string, object>;
        return { ...tool, description: tool.description?.replace("The default exclusive mode allows one owner.",
          "Codex defaults to a concurrent region. Choose exclusive for tools that need the whole desktop."),
          inputSchema: { ...tool.inputSchema, properties: { ...properties,
            mode: { ...properties.mode, description: "layered (default background), exclusive (whole background desktop), or foreground (main Windows desktop, only at the user's explicit request). Only acquire. Release your current lease before switching modes." } } } };
      }) };
    }
    case "tools/call": {
      const params = request.params as { name?: string; arguments?: Record<string, unknown> } | undefined;
      const name = params?.name;
      if (typeof name !== "string" || !name) throw new McpError(rpc.invalidParams, "A tool name is required.");
      const args = params.arguments ?? {};
      if (typeof args !== "object" || Array.isArray(args)) throw new McpError(rpc.invalidParams, "Tool arguments must be an object.");
      if (name === "seat_element") {
        return readback.element(args,
          action => native.callTool(name, action, { signal, timeoutMs: desktopToolTimeout(name, action) }),
          windowId => native.callTool("seat_observe", { windowId, includeScreenshot: false }, { signal, timeoutMs: 3000 }));
      }
      const result = await native.callTool(name, seatModelArguments(name, args), { signal, timeoutMs: desktopToolTimeout(name, args) });
      const details = result.structuredContent as { matched?: boolean } | undefined;
      if (name === "seat_observe" || name === "seat_wait" && details?.matched === true) {
        readback.remember(args.windowId, result);
      }
      if (!result.isError && (name === "seat_stop" || name === "seat_lease" && args.action === "release")) readback.clear();
      return result;
    }
    default:
      throw new McpError(rpc.methodNotFound, "Unsupported MCP method: " + request.method);
  }
}

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on("line", line => {
  let message;
  try { message = parseJsonRpcMessage(JSON.parse(line)); }
  catch { send({ id: null, error: { code: rpc.parseError, message: "Invalid MCP message." } }); return; }
  if (!("method" in message)) return;
  if (!("id" in message)) {
    if (message.method === "notifications/cancelled") {
      const { requestId } = (message.params ?? {}) as { requestId?: JsonRpcId };
      if (requestId !== undefined) pending.get(requestId)?.abort();
    }
    return;
  }
  const request = message;
  const controller = new AbortController();
  pending.set(request.id, controller);
  void handle(request, controller.signal).then(result => {
    if (!controller.signal.aborted) send({ id: request.id, result });
  }, error => {
    if (!controller.signal.aborted) send({ id: request.id, error: {
      code: error instanceof McpError ? error.code : rpc.internalError,
      message: error instanceof Error ? error.message : String(error),
    } });
  }).finally(() => pending.delete(request.id));
});
input.once("close", () => {
  for (const controller of pending.values()) controller.abort();
  void native.close();
});
