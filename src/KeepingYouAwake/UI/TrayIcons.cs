using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using KeepingYouAwake.Windows;
using Microsoft.Win32;

namespace KeepingYouAwake.UI;

/// <summary>
/// Builds the tray icons from the cup artwork: orange when active, and white or black
/// to match the taskbar theme when inactive.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private static readonly Color ActiveColor = Color.FromArgb(0xF2, 0x6B, 0x1D);
    private readonly List<IntPtr> handles = [];
    private readonly List<IntPtr> stale = [];

    public TrayIcons() => Rebuild();

    public Icon Active { get; private set; } = SystemIcons.Application;
    public Icon Inactive { get; private set; } = SystemIcons.Application;

    public static Icon AppIcon()
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("AppIcon.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    /// <summary>Re-renders for the current theme. Call <see cref="ReleaseStale"/> once the tray shows the new icons.</summary>
    public void Rebuild()
    {
        var size = SystemInformation.SmallIconSize;
        var light = IsTaskbarLight();
        stale.AddRange(handles);
        handles.Clear();
        Active = Render("TrayActive.png", ActiveColor, size);
        Inactive = Render("TrayInactive.png", light ? Color.Black : Color.White, size);
    }

    public void ReleaseStale()
    {
        foreach (var handle in stale) { NativeMethods.DestroyIcon(handle); }
        stale.Clear();
    }

    private Icon Render(string resource, Color color, Size size)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(resource);
        if (stream is null) { return SystemIcons.Application; }
        using var source = new Bitmap(stream);
        using var canvas = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var scale = Math.Min((float)size.Width / source.Width, (float)size.Height / source.Height);
            var w = source.Width * scale;
            var h = source.Height * scale;
            var dest = new RectangleF((size.Width - w) / 2, (size.Height - h) / 2, w, h);

            // The artwork is a black template image: keep its alpha, replace its colour.
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix(
            [
                [0, 0, 0, 0, 0],
                [0, 0, 0, 0, 0],
                [0, 0, 0, 0, 0],
                [0, 0, 0, 1, 0],
                [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
            ]));
            g.DrawImage(source, Rectangle.Round(dest), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        var handle = canvas.GetHicon();
        handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    public void Dispose()
    {
        ReleaseStale();
        foreach (var handle in handles) { NativeMethods.DestroyIcon(handle); }
        handles.Clear();
    }
}
