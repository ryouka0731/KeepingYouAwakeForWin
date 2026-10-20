using KeepingYouAwake.Core;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake.UI;

/// <summary>
/// Every modal question the Settings window asks. Component tests substitute scripted
/// answers; <see cref="WinFormsPrompts"/> shows the real dialogs.
/// </summary>
internal interface IUserPrompts
{
    ActivationDuration? AskDuration(IWin32Window owner);
    ScheduleWindow? EditScheduleWindow(IWin32Window owner, ScheduleWindow? window);
    string? AskText(IWin32Window owner, string title, string label);
    string? ChooseRunningApplication(IWin32Window owner);
    string? ChooseFile(IWin32Window owner, string title, string filter, string initialDirectory);
    string? ChooseFolder(IWin32Window owner, string description, string initialDirectory);
    bool Confirm(IWin32Window owner, string message, string title);
    void Inform(IWin32Window owner, string message, string title, MessageBoxIcon icon = MessageBoxIcon.None);
    void OpenUrl(string url);
}

/// <summary>The real dialogs.</summary>
internal sealed class WinFormsPrompts : IUserPrompts
{
    public static WinFormsPrompts Instance { get; } = new();

    public ActivationDuration? AskDuration(IWin32Window owner)
    {
        using var dialog = new AddDurationDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Duration : null;
    }

    public ScheduleWindow? EditScheduleWindow(IWin32Window owner, ScheduleWindow? window)
    {
        using var dialog = new ScheduleWindowDialog(window);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Window : null;
    }

    public string? AskText(IWin32Window owner, string title, string label)
    {
        using var dialog = new TextPromptDialog(title, label);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Value : null;
    }

    public string? ChooseRunningApplication(IWin32Window owner)
    {
        using var dialog = new RunningAppDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedExecutable : null;
    }

    public string? ChooseFile(IWin32Window owner, string title, string filter, string initialDirectory)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = initialDirectory };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public string? ChooseFolder(IWin32Window owner, string description, string initialDirectory)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, InitialDirectory = initialDirectory };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public bool Confirm(IWin32Window owner, string message, string title) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    public void Inform(IWin32Window owner, string message, string title, MessageBoxIcon icon = MessageBoxIcon.None) =>
        MessageBox.Show(owner, message, title, MessageBoxButtons.OK, icon);

    public void OpenUrl(string url) => ShellIntegration.OpenUrl(url);
}
