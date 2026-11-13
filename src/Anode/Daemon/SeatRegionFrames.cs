namespace Anode.Daemon;

internal readonly record struct SeatRegion(int Number, Rectangle Bounds, bool Occupied);

/// <summary>Draws only the frame pixels above the native RDP control, without receiving input.</summary>
internal sealed class SeatRegionFrames : Control
{
    private SeatRegion[] _frames = [];
    private bool _controlling;
    private int _stroke, _badgeWidth, _badgeHeight;

    public SeatRegionFrames()
    {
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Name = "SeatRegionFrames";
        TabStop = false;
        Enabled = false;
        Visible = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) // WM_NCHITTEST: let the native viewer under this child receive the click.
        {
            m.Result = new IntPtr(-1); // HTTRANSPARENT
            return;
        }
        base.WndProc(ref m);
    }

    internal static Rectangle Project(Rectangle region, Size source, Size viewport)
    {
        int X(int x) => (int)((long)x * viewport.Width / source.Width);
        int Y(int y) => (int)((long)y * viewport.Height / source.Height);
        return Rectangle.FromLTRB(X(region.Left), Y(region.Top), X(region.Right), Y(region.Bottom));
    }

    public void UpdateFrames(Rectangle viewport, Size source, IReadOnlyList<SeatRegion> regions, bool controlling)
    {
        int stroke = Math.Max(2, LogicalToDeviceUnits(3));
        int badgeWidth = LogicalToDeviceUnits(26), badgeHeight = LogicalToDeviceUnits(22);
        var frames = regions.Select(region => region with { Bounds = Project(region.Bounds, source, viewport.Size) })
            .Where(region => region.Bounds.Width >= badgeWidth && region.Bounds.Height >= badgeHeight).ToArray();
        bool geometryChanged = Bounds != viewport || _stroke != stroke || _badgeWidth != badgeWidth
            || _badgeHeight != badgeHeight || !_frames.SequenceEqual(frames);
        bool colorChanged = _controlling != controlling;
        _controlling = controlling;
        if (geometryChanged)
        {
            _frames = frames;
            _stroke = stroke;
            _badgeWidth = badgeWidth;
            _badgeHeight = badgeHeight;
            Bounds = viewport;

            var mask = new Region();
            mask.MakeEmpty();
            foreach (var frame in frames)
            {
                var b = frame.Bounds;
                mask.Union(new Rectangle(b.Left, b.Top, b.Width, stroke));
                mask.Union(new Rectangle(b.Left, b.Bottom - stroke, b.Width, stroke));
                mask.Union(new Rectangle(b.Left, b.Top, stroke, b.Height));
                mask.Union(new Rectangle(b.Right - stroke, b.Top, stroke, b.Height));
                mask.Union(Badge(b));
            }
            Region = mask;
        }
        Visible = frames.Length > 0;
        if (geometryChanged || colorChanged) Invalidate();
    }

    private Rectangle Badge(Rectangle bounds) => new(bounds.Left, bounds.Top, _badgeWidth, _badgeHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        using var ink = new SolidBrush(Color.FromArgb(15, 23, 42));
        using var idle = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var active = new SolidBrush(_controlling ? Color.FromArgb(45, 212, 191) : Color.FromArgb(226, 232, 240));
        foreach (var frame in _frames)
        {
            var bounds = frame.Bounds;
            var line = frame.Occupied || _controlling ? active : idle;
            // The two tones keep the line visible on both bright and dark apps.
            e.Graphics.FillRectangle(ink, bounds);
            var inset = Rectangle.Inflate(bounds, -1, -1);
            e.Graphics.FillRectangle(line, inset);
            var badge = Badge(bounds);
            e.Graphics.FillRectangle(ink, badge);
            TextRenderer.DrawText(e.Graphics, frame.Number.ToString(), Font, badge, line.Color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
