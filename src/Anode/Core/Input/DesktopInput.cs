using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Capture;

namespace Anode.Core.Input;

internal static class DesktopInput
{
    public static JsonObject Dispatch(string op, JsonObject r, CancellationToken cancel, bool foreground = false)
    {
        if (foreground && op != "screenshot") ValidateForegroundInput(op, r);
        switch (op)
        {
            case "input.text":
                return InputInjector.TypeText(r.Str("text")!, r.Int("perCharMs"), r.Int("timeoutMs") ?? 60_000, cancel,
                    foreground ? () => !ViewerFocused() : null);

            case "screenshot":
            {
                var shot = ScreenCapture.Capture(
                    maxWidth: r.Int("maxWidth"),
                    format: r.Str("format") ?? "png",
                    jpegQuality: r.Int("quality") ?? 80, cancel: cancel, timeoutMs: Math.Min(5000, r.Int("timeoutMs") ?? 60000));

                return JsonLine.Ok(new JsonObject
                {
                    ["mimeType"] = shot.MimeType,
                    ["width"] = shot.Width,
                    ["height"] = shot.Height,
                    ["sourceWidth"] = shot.SourceWidth,
                    ["sourceHeight"] = shot.SourceHeight,
                    ["bytes"] = shot.Bytes.Length,
                    ["data"] = Convert.ToBase64String(shot.Bytes)
                });
            }

            case "input.move":
            {
                int? dx = r.Int("dx");
                int? dy = r.Int("dy");
                if (dx.HasValue || dy.HasValue)
                {
                    // Relative motion, which is what games reading raw input expect.
                    InputInjector.MoveBy(dx ?? 0, dy ?? 0);
                }
                else
                {
                    int x = r.Int("x") ?? throw new ArgumentException("move needs x and y, or dx and dy.");
                    int y = r.Int("y") ?? throw new ArgumentException("move needs x and y, or dx and dy.");
                    InputInjector.MoveTo(x, y);
                }
                var at = InputInjector.CursorPosition();
                return JsonLine.Ok(new JsonObject { ["x"] = at.X, ["y"] = at.Y });
            }

            case "input.click":
            {
                InputInjector.Click(
                    r.Str("button") ?? "left",
                    r.Int("x"), r.Int("y"),
                    r.Int("count") ?? 1,
                    r.Int("holdMs") ?? 20, cancel, foreground ? () => !ViewerFocused() && !ViewerAt(ClickPoint(r)) : null);
                return JsonLine.Ok();
            }

            case "input.down":
                InputInjector.MouseDown(r.Str("button") ?? "left", r.Int("x"), r.Int("y"));
                return JsonLine.Ok();

            case "input.up":
                InputInjector.MouseUp(r.Str("button") ?? "left", r.Int("x"), r.Int("y"));
                return JsonLine.Ok();

            case "input.drag":
            {
                var points = DragPath.Read(r);
                var bounds = ScreenCapture.SeatBounds();
                if (foreground && points.Any(point => !bounds.Contains(point)))
                    throw new ArgumentException("Foreground drag points must be inside the primary monitor shown by seat_screenshot.");
                if (foreground && points.Any(ViewerAt))
                    throw new InvalidOperationException("Foreground drag points cannot target the EVO Seat viewer.");
                // Whole-seat drags have always clamped coordinates to the active desktop.
                for (int i = 0; i < points.Length; i++)
                    points[i] = new Point(Math.Clamp(points[i].X, 0, bounds.Width - 1), Math.Clamp(points[i].Y, 0, bounds.Height - 1));
                InputInjector.Drag(points, r.Str("button") ?? "left", cancel, foreground ? () => !ViewerFocused() : null,
                    foreground ? point => !ViewerAt(point) : null);
                return JsonLine.Ok();
            }

            case "input.scroll":
            {
                int amount = r.Int("amount") ?? -3;
                bool horizontal = r.Bool("horizontal") ?? false;
                long notches = Math.Abs((long)amount);
                string direction = horizontal ? (amount < 0 ? "left" : "right") : (amount < 0 ? "down" : "up");
                return JsonLine.Ok(ScreenSettle.Real(() => InputInjector.Scroll(amount, horizontal),
                    $"Scrolled {notches} {(notches == 1 ? "notch" : "notches")} {direction}", r.Int("timeoutMs") ?? 60_000, cancel));
            }

            case "input.key":
            {
                string keys = r.Str("keys") ?? throw new ArgumentException("key needs 'keys', for example \"ctrl+s\".");
                void Press() => InputInjector.Press(keys, r.Int("holdMs") ?? 40, r.Bool("scanCode") ?? true, cancel);
                if (ScreenSettle.IsScrollingKey(keys))
                    return JsonLine.Ok(ScreenSettle.Real(Press, $"Pressed {keys}", r.Int("timeoutMs") ?? 60_000, cancel));
                Press();
                return JsonLine.Ok();
            }

            case "input.keydown":
                InputInjector.KeyDown(r.Str("key") ?? throw new ArgumentException("keydown needs 'key'."),
                    r.Bool("scanCode") ?? true);
                return JsonLine.Ok();

            case "input.keyup":
                InputInjector.KeyUp(r.Str("key") ?? throw new ArgumentException("keyup needs 'key'."),
                    r.Bool("scanCode") ?? true);
                return JsonLine.Ok();

            default:
                throw new ArgumentException($"Unknown desktop input operation '{op}'.");
        }
    }

    private static void ValidateForegroundInput(string op, JsonObject request)
    {
        if (ViewerFocused())
            throw new InvalidOperationException("The EVO Seat viewer has focus. Focus the intended main-desktop app before sending input.");
        if (op is not ("input.move" or "input.click" or "input.scroll")) return;
        var bounds = ScreenCapture.SeatBounds();
        void Check(string x, string y)
        {
            if (request.Int(x) is not int px || request.Int(y) is not int py) return;
            if (!bounds.Contains(px, py))
                throw new ArgumentException("Foreground coordinates must be inside the primary monitor shown by seat_screenshot.");
            CheckViewer(new Point(px, py));
        }
        if (op == "input.scroll" || op == "input.click" && request.Int("x") is null)
            CheckViewer(InputInjector.CursorPosition());
        Check("x", "y");
    }

    private static void CheckViewer(Point point)
    {
        if (ViewerAt(point))
            throw new InvalidOperationException("Foreground pointer input cannot target the EVO Seat viewer.");
    }

    private static Point ClickPoint(JsonObject request) =>
        request.Int("x") is int x && request.Int("y") is int y ? new Point(x, y) : InputInjector.CursorPosition();

    private static bool ViewerAt(Point point) =>
        Desktop.WindowAccess.IsSeatViewer(Desktop.WindowAccess.WindowFromPoint(point));

    private static bool ViewerFocused() =>
        Desktop.WindowAccess.IsSeatViewer(Native.Native.GetForegroundWindow());
}
