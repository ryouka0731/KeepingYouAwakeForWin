using KeepingYouAwake.Core;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake.UI;

/// <summary>Base for the small modal dialogs: OK / Cancel row under a content column.</summary>
internal abstract class DialogBase : Form
{
    protected DialogBase(string title)
    {
        Text = title;
        Icon = TrayIcons.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Content = Ui.Column();
        Controls.Add(Content);
    }

    protected FlowLayoutPanel Content { get; }

    /// <summary>Adds the OK / Cancel buttons. <paramref name="validate"/> returns an error message or null.</summary>
    protected void AddButtons(Func<string?> validate)
    {
        var row = Ui.Row();
        row.Margin = new Padding(0, 12, 0, 0);
        var ok = new Button { Text = Strings.Get("OK"), AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        var cancel = new Button { Text = Strings.Get("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (validate() is { } error)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        row.Controls.Add(ok);
        row.Controls.Add(cancel);
        Content.Controls.Add(row);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

internal sealed class AddDurationDialog : DialogBase
{
    private readonly NumericUpDown hours = new() { Minimum = 0, Maximum = 999, Width = 70 };
    private readonly NumericUpDown minutes = new() { Minimum = 0, Maximum = 59, Width = 70 };
    private readonly NumericUpDown seconds = new() { Minimum = 0, Maximum = 59, Width = 70 };

    public AddDurationDialog() : base(Strings.Get("AddDuration"))
    {
        var row = Ui.Row();
        foreach (var (field, label) in new[] { (hours, "Hours"), (minutes, "Minutes"), (seconds, "Seconds") })
        {
            row.Controls.Add(field);
            row.Controls.Add(new Label { Text = Strings.Get(label), AutoSize = true, Margin = new Padding(2, 6, 12, 0) });
        }
        minutes.Value = 45;
        Content.Controls.Add(row);
        AddButtons(() => Duration is null ? Strings.Get("DurationInvalid") : null);
    }

    public ActivationDuration? Duration =>
        ActivationDuration.FromComponents((int)hours.Value, (int)minutes.Value, (int)seconds.Value);
}

internal sealed class TextPromptDialog : DialogBase
{
    private readonly TextBox input = new() { Width = 320 };

    public TextPromptDialog(string title, string label) : base(title)
    {
        Content.Controls.Add(Ui.Text(label));
        Content.Controls.Add(input);
        AddButtons(() => null);
    }

    public string Value => input.Text;
}

/// <summary>Edits one Active Hours window: weekdays plus a start and end time.</summary>
internal sealed class ScheduleWindowDialog : DialogBase
{
    private readonly CheckBox[] days = new CheckBox[7];
    private readonly DateTimePicker from = TimePicker();
    private readonly DateTimePicker to = TimePicker();
    private readonly bool enabled;

    public ScheduleWindowDialog(ScheduleWindow? window) : base(Strings.Get("ActiveHoursWindow"))
    {
        var w = window ?? new ScheduleWindow
        {
            Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            StartMinutes = 9 * 60,
            EndMinutes = 18 * 60,
        };
        enabled = w.Enabled;

        Content.Controls.Add(Ui.Text(Strings.Get("Weekdays")));
        var dayRow = Ui.Row();
        // Monday first, Sunday last.
        foreach (var day in Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7)))
        {
            var box = new CheckBox { Text = Strings.WeekdayName(day), AutoSize = true, Checked = w.Weekdays.Contains(day) };
            days[(int)day] = box;
            dayRow.Controls.Add(box);
        }
        Content.Controls.Add(dayRow);

        var timeRow = Ui.Row();
        timeRow.Controls.Add(new Label { Text = Strings.Get("From"), AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        timeRow.Controls.Add(from);
        timeRow.Controls.Add(new Label { Text = Strings.Get("To"), AutoSize = true, Margin = new Padding(12, 6, 4, 0) });
        timeRow.Controls.Add(to);
        Content.Controls.Add(timeRow);
        from.Value = DateTime.Today.AddMinutes(w.StartMinutes);
        to.Value = DateTime.Today.AddMinutes(w.EndMinutes);

        Content.Controls.Add(Ui.Hint(Strings.Get("ActiveHoursHint"), 360));
        AddButtons(() => days.Any(d => d.Checked) ? null : Strings.Get("SelectAtLeastOneDay"));
    }

    public ScheduleWindow Window => new ScheduleWindow
    {
        Weekdays = Enumerable.Range(0, 7).Where(i => days[i].Checked).Select(i => (DayOfWeek)i).ToList(),
        StartMinutes = from.Value.Hour * 60 + from.Value.Minute,
        EndMinutes = to.Value.Hour * 60 + to.Value.Minute,
        Enabled = enabled,
    }.Normalized();

    private static DateTimePicker TimePicker() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 80,
    };
}

/// <summary>Lists this session's running programs so one can be picked as a watched application.</summary>
internal sealed class RunningAppDialog : DialogBase
{
    private readonly ListBox list = new() { Width = 360, Height = 300, IntegralHeight = false, Sorted = true };

    public RunningAppDialog() : base(Strings.Get("ChooseApplication"))
    {
        foreach (var name in new SessionProcessLister().RunningExecutableNames()) { list.Items.Add(name); }
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not null) { DialogResult = DialogResult.OK; }
        };
        Content.Controls.Add(list);
        AddButtons(() => list.SelectedItem is null ? Strings.Get("ChooseApplication") : null);
    }

    public string? SelectedExecutable => list.SelectedItem as string;
}

/// <summary>Recent sessions from the activity log, newest first.</summary>
internal sealed class ActivityLogForm : Form
{
    private readonly ActivityLogger log;
    private readonly ListView view = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
    };

    public ActivityLogForm(ActivityLogger log)
    {
        this.log = log;
        Text = Strings.Get("ActivityLogTitle");
        Icon = TrayIcons.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        ClientSize = new Size(760, 420);

        view.Columns.Add(Strings.Get("ColumnStarted"), 150);
        view.Columns.Add(Strings.Get("ColumnEnded"), 150);
        view.Columns.Add(Strings.Get("ColumnSource"), 150);
        view.Columns.Add(Strings.Get("ColumnRequested"), 130);
        view.Columns.Add(Strings.Get("ColumnReason"), 140);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(Ui.Button(Strings.Get("Close"), (_, _) => Close()));
        buttons.Controls.Add(Ui.Button(Strings.Get("OpenLogFolder"), (_, _) =>
            ShellIntegration.OpenUrl(Path.GetDirectoryName(log.FilePath)!)));
        buttons.Controls.Add(Ui.Button(Strings.Get("Refresh"), (_, _) => Reload()));

        Controls.Add(view);
        Controls.Add(buttons);
        Activated += (_, _) => Reload();
    }

    private void Reload()
    {
        view.BeginUpdate();
        view.Items.Clear();
        foreach (var entry in log.RecentEntries(500))
        {
            var requested = entry.RequestedDuration < 0
                ? Strings.Get("Indefinitely")
                : Strings.DurationText(TimeSpan.FromSeconds(Math.Round(entry.RequestedDuration)));
            view.Items.Add(new ListViewItem(
            [
                entry.StartedAt.LocalDateTime.ToString("g"),
                entry.EndedAt?.LocalDateTime.ToString("g") ?? Strings.Get("StillActive"),
                Strings.SourceName(entry.Source),
                requested,
                Strings.ReasonName(entry.EndedReason),
            ]));
        }
        view.EndUpdate();
    }
}
