export function seatModelArguments(tool: string, args: Record<string, unknown>) {
  if (tool === "seat_observe" && args.includeScreenshot === undefined) {
    return { ...args, includeScreenshot: false };
  }
  if (tool === "seat_screenshot" && args.format === undefined) {
    return { maxWidth: 1280, ...args, format: "jpeg" };
  }
  return args;
}
