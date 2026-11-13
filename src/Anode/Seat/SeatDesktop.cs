using System.Runtime.InteropServices;
using System.Text;
using Anode.Core.Launch;
using Anode.Core.Util;
using Win = Anode.Core.Native.Native;

namespace Anode.Seat;

/// <summary>A separate desktop inside the verified Windows child session.</summary>
internal static class SeatDesktop
{
    private const int ObjectName = 2;
    private const uint ReadObjects = 0x0001;
    private const uint DesktopAccess = 0x01ff;

    private static string Name => "EVO-Seat-" + Env.Channel;

    internal static int LaunchHost()
    {
        var desktop = CreateDesktop(Name, IntPtr.Zero, IntPtr.Zero, 0, DesktopAccess, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw Win.LastError("Create the EVO Seat desktop");
        try
        {
            Activate(desktop);
            int pid = DetachedProcess.Start(Env.ExecutablePath,
                ["--channel", Env.Channel, "__seat-host", "--state-dir", Env.StateDirectory],
                AppContext.BaseDirectory, desktop: @"WinSta0\" + Name);
            // Keep the desktop alive until the host attaches. A failed or exited host releases this handle too.
            try
            {
                using var host = System.Diagnostics.Process.GetProcessById(pid);
                host.WaitForExit();
            }
            catch (ArgumentException) { } // The host already exited.
            return 0;
        }
        finally { CloseDesktop(desktop); }
    }

    private static void Activate(IntPtr desktop)
    {
        // RDP's login callback can arrive while Winlogon still owns the input desktop.
        var deadline = Environment.TickCount64 + 8_000;
        do
        {
            if (SwitchDesktop(desktop)) return;
            Thread.Sleep(200);
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException("Windows did not let EVO Seat switch to its desktop after sign-in.");
    }

    internal static void VerifyHostDesktop()
    {
        if (!HasName(GetThreadDesktop(GetCurrentThreadId()), Name))
            throw new InvalidOperationException("The seat host did not start on its own desktop.");
    }

    internal static bool IsActive()
        => IsActive(Name);

    internal static bool IsActive(string name)
    {
        var desktop = OpenInputDesktop(0, false, ReadObjects);
        if (desktop == IntPtr.Zero) return false;
        try { return HasName(desktop, name); }
        finally { CloseDesktop(desktop); }
    }

    internal static bool IsInteractiveDefault()
        => HasName(GetProcessWindowStation(), "WinSta0")
            && HasName(GetThreadDesktop(GetCurrentThreadId()), "Default") && IsActive("Default");

    private static bool HasName(IntPtr desktop, string expected)
    {
        var name = new StringBuilder(128);
        return desktop != IntPtr.Zero && GetUserObjectInformation(desktop, ObjectName, name,
            name.Capacity * sizeof(char), out _) && name.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder name, int length, out int needed);
}
