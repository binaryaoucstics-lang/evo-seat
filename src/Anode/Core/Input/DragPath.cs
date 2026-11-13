using System.Text.Json.Nodes;
using Anode.Core.Bridge;

namespace Anode.Core.Input;

internal static class DragPath
{
    internal const int MaxPoints = 128;
    internal const int MaxMoves = 512;
    internal const int StepMs = 6;
    private const int PixelsPerMove = 8;
    private const int MaxMovesPerSegment = 24;

    // The tool schema validates every coordinate before the request reaches input.
    internal static Point[] Read(JsonObject request)
    {
        if (request["points"] is JsonArray points)
            return points.Cast<JsonObject>().Select(point => new Point(point.Int("x")!.Value, point.Int("y")!.Value)).ToArray();
        return [new Point(request.Int("fromX")!.Value, request.Int("fromY")!.Value),
            new Point(request.Int("toX")!.Value, request.Int("toY")!.Value)];
    }

    internal static Point[] Moves(IReadOnlyList<Point> points)
    {
        var moves = new List<Point>();
        for (int i = 1; i < points.Count; i++)
        {
            var start = points[i - 1];
            var end = points[i];
            if (start == end) continue;

            double dx = (double)end.X - start.X, dy = (double)end.Y - start.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            int count = (int)Math.Min(MaxMovesPerSegment, Math.Ceiling(distance / PixelsPerMove));
            if (moves.Count + count > MaxMoves)
                throw new ArgumentException($"The drag needs more than {MaxMoves} moves. Shorten the path or use fewer waypoints.");
            for (int step = 1; step < count; step++)
            {
                double fraction = (double)step / count;
                moves.Add(new Point((int)Math.Round(start.X + dx * fraction), (int)Math.Round(start.Y + dy * fraction)));
            }
            moves.Add(end);
        }
        return moves.ToArray();
    }
}
