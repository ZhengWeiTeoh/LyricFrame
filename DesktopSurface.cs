using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class DesktopSurface : Form
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string name, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint color, byte opacity, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref Point p);
    delegate bool WindowVisitor(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor callback, IntPtr data);

    IntPtr parent, icons;
    Rectangle lastBounds;
    Action<Graphics> draw;
    bool layered;
    public bool Attached { get { return IsHandleCreated && GetParent(Handle) == parent; } }

    DesktopSurface()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    public static DesktopSurface Attach(Rectangle bounds)
    {
        var desktop = FindWindow("Progman", null);
        if (desktop == IntPtr.Zero) return null;
        IntPtr result;
        SendMessageTimeout(desktop, 0x052C, new IntPtr(13), new IntPtr(1), 2, 500, out result);
        var raised = (GetWindowLong(desktop, -20) & 0x00200000) != 0;
        var icons = FindWindowEx(desktop, IntPtr.Zero, "SHELLDLL_DefView", null);
        var worker = IntPtr.Zero;
        if (!raised)
        {
            EnumWindows(delegate(IntPtr window, IntPtr ignored)
            {
                if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
                worker = FindWindowEx(IntPtr.Zero, window, "WorkerW", null);
                return false;
            }, IntPtr.Zero);
        }
        var parent = raised && icons != IntPtr.Zero ? desktop : worker;
        if (parent == IntPtr.Zero) return null;

        var surface = new DesktopSurface {parent = parent, icons = raised ? icons : IntPtr.Zero, layered = raised};
        var handle = surface.Handle;
        SetWindowLong(handle, -16, (GetWindowLong(handle, -16) & 0x7fffffff) | 0x40000000);
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000020 | (raised ? 0x80000 : 0));
        SetParent(handle, parent);
        if (!surface.Attached || (raised && !SetLayeredWindowAttributes(handle, 0, 255, 2)) ||
            !surface.Position(bounds))
        {
            surface.Dispose();
            return null;
        }
        return surface;
    }

    bool Position(Rectangle bounds)
    {
        var origin = bounds.Location;
        if (!ScreenToClient(parent, ref origin)) return false;
        lastBounds = bounds;
        return SetWindowPos(Handle, icons, origin.X, origin.Y, bounds.Width, bounds.Height,
            0x0010u | 0x0040u | (layered ? 0u : 0x0004u));
    }

    public void Render(Rectangle bounds, Rectangle dirty, Action<Graphics> paint)
    {
        if (bounds != lastBounds) Position(bounds);
        draw = paint;
        Invalidate(dirty);
        Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (draw != null) draw(e.Graphics);
        else e.Graphics.Clear(Color.Black);
    }
}
