using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WOMicNext;

internal static class TrayIcons
{
    private static readonly Color Brand = Color.FromArgb(47, 91, 149);
    private static readonly Color Connected = Color.FromArgb(23, 132, 88);
    private static readonly Color Disconnected = Color.FromArgb(188, 45, 59);
    private static readonly Color Pending = Color.FromArgb(163, 106, 17);

    public static Icon Create(ConnectionState state, bool microphoneEnabled, bool taskbar = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            var fill = taskbar ? Brand : state switch
            {
                ConnectionState.Connected => Connected,
                ConnectionState.Disconnected => Disconnected,
                _ => Pending
            };
            using (var background = new SolidBrush(fill)) graphics.FillEllipse(background, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.FillRoundedRectangle(white, new Rectangle(13, 6, 6, 12), 3);
            graphics.DrawArc(pen, 9, 10, 14, 13, 0, 180);
            graphics.DrawLine(pen, 16, 23, 16, 26);
            graphics.DrawLine(pen, 12, 27, 20, 27);
            if (!microphoneEnabled)
            {
                using var slash = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(slash, 7, 25, 25, 7);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
