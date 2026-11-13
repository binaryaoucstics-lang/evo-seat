using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Anode.Core.Capture;

/// <summary>
/// Grabs the seat's screen. Runs inside the seat host, so "the screen" is the child
/// session's own desktop and never the user's monitors.
///
/// Downscaling and JPEG are offered because the usual consumer is a model with a
/// token budget: a 1920x1080 PNG is an expensive way to answer "is the game loaded yet".
/// </summary>
internal static class ScreenCapture
{
    public sealed record Shot(byte[] Bytes, string MimeType, int Width, int Height, int SourceWidth, int SourceHeight);

    private static readonly CaptureReadiness Readiness = new();
    private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() =>
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid));

    public static Shot Capture(int? maxWidth = null, string format = "png", int jpegQuality = 80, Rectangle? region = null,
        CancellationToken cancel = default, int timeoutMs = 5000)
    {
        Rectangle source = region ?? SeatBounds();
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("The seat reported an empty desktop. It may still be signing in.");

        var pixelFormat = IsJpeg(format) ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb;
        using var raw = new Bitmap(source.Width, source.Height, pixelFormat);
        using (var graphics = Graphics.FromImage(raw))
        {
            Readiness.Run(() => { graphics.CopyFromScreen(source.Location, Point.Empty, source.Size, CopyPixelOperation.SourceCopy); return true; },
                IsSessionDisconnected, timeoutMs, cancel);
        }
        return Encode(raw, maxWidth, format, jpegQuality);
    }

    internal static string UnavailableMessage(bool disconnected) => disconnected
        ? "The seat's display is unavailable for capture because its viewer is disconnected, so Windows has stopped drawing the seat. "
            + "Programs and command jobs keep running. seat_status (lastError) says why and how to reconnect; if the seat signed in with a password, only the user can. "
            + "No foreground capture or viewer activation was attempted. Use seat_observe with includeScreenshot=false for accessible native controls; do not guess screen coordinates."
        : "The seat's display is unavailable for capture. RDP may have suspended rendering while its viewer was hidden or minimized, "
            + "or the seat may be locked. Restarting the updated Anode daemon applies its background-rendering preference. "
            + "No foreground capture or viewer activation was attempted. Use seat_observe with includeScreenshot=false for accessible controls; do not guess screen coordinates.";

    private static bool IsSessionDisconnected()
    {
        const uint currentSession = uint.MaxValue; // WTS_CURRENT_SESSION
        const int connectState = 8, disconnected = 4; // WTSConnectState, WTSDisconnected
        IntPtr buffer = IntPtr.Zero;
        try
        {
            return WTSQuerySessionInformation(Native.Native.CurrentServer, currentSession, connectState, out buffer, out uint bytes)
                && buffer != IntPtr.Zero && bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == disconnected;
        }
        catch { return false; } // A failed query leaves the cause unknown.
        finally { if (buffer != IntPtr.Zero) Native.Native.WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass,
        out IntPtr buffer, out uint bytesReturned);

    /// <summary>A picture scaled down to maxWidth, if wider, and encoded; source sizes are the picture's own.</summary>
    public static Shot Encode(Bitmap raw, int? maxWidth, string format, int jpegQuality)
    {
        var source = new Size(raw.Width, raw.Height);
        bool jpeg = IsJpeg(format);
        int width = maxWidth is int limit && limit > 0 ? Math.Min(limit, source.Width) : source.Width;
        int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
        bool scaled = width < source.Width;
        // Scale directly into the encoder's pixel format. Native JPEG captures are already RGB.
        bool needsCopy = scaled || jpeg && raw.PixelFormat != PixelFormat.Format24bppRgb;
        using var converted = needsCopy
            ? new Bitmap(width, height, jpeg ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb)
            : null;
        if (converted is not null)
        {
            using var graphics = Graphics.FromImage(converted);
            if (jpeg) graphics.Clear(Color.Black);
            if (scaled)
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(raw, 0, 0, width, height);
            }
            else graphics.DrawImageUnscaled(raw, 0, 0);
        }

        var output = converted ?? raw;
        using var buffer = new MemoryStream();
        if (jpeg)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
            output.Save(buffer, JpegEncoder.Value, parameters);
        }
        else output.Save(buffer, ImageFormat.Png);

        return new Shot(buffer.ToArray(), jpeg ? "image/jpeg" : "image/png", width, height, source.Width, source.Height);
    }

    private static bool IsJpeg(string format) => format.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
        || format.Equals("jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rectangle a screenshot covers. Deliberately the primary display rather than
    /// the whole virtual desktop, because that is the coordinate space mouse injection
    /// uses: a pixel in a screenshot is the pixel a click with the same x and y hits.
    /// A child session only ever has one display, so inside the seat the two agree.
    /// It is read afresh each time, never from WinForms' cached screens, because an agent
    /// can change the seat's resolution while the seat host runs.
    /// </summary>
    public static Rectangle SeatBounds() => new(Point.Empty, Input.InputInjector.ScreenSize());
}
