namespace KeepingYouAwake.UI;

/// <summary>Small helpers that build auto-sizing, DPI-friendly WinForms layouts in code.</summary>
internal static class Ui
{
    public static FlowLayoutPanel Column(Padding? padding = null) => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = padding ?? new Padding(12),
        Margin = new Padding(0),
    };

    public static FlowLayoutPanel Row() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = new Padding(0, 2, 0, 2),
    };

    /// <summary>A tab page whose content scrolls when the window is smaller than it.</summary>
    public static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { AutoScroll = true, UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    public static Label Title(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 2),
    };

    public static Label Hint(string text, int width = 460) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(20, 0, 0, 8),
    };

    public static Label Text(string text, int width = 480) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 4, 0, 4),
    };

    public static CheckBox Check(string text, int width = 480) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 4, 0, 2),
    };

    public static Button Button(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 6, 4) };
        button.Click += onClick;
        return button;
    }

    /// <summary>Shows a form, or brings it to the front when already open.</summary>
    public static void ShowAndActivate(this Form form)
    {
        if (!form.Visible) { form.Show(); }
        if (form.WindowState == FormWindowState.Minimized) { form.WindowState = FormWindowState.Normal; }
        form.Activate();
        form.BringToFront();
    }
}
