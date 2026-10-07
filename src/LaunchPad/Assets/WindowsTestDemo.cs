using System;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;

// Built into a small standalone Windows executable, not a PowerShell script.
internal static class WindowsTestDemo
{
    [STAThread]
    private static void Main()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demo-started.txt"), "Managed Main reached. PID: " + System.Diagnostics.Process.GetCurrentProcess().Id);
            RunChecklist();
        }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demo-startup-error.txt"), error.ToString()); } catch { }
            Environment.ExitCode = 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunChecklist()
    {
        Application.EnableVisualStyles();
        var form = new Form { Text = "LaunchPad checklist demo", Width = 560, Height = 430, StartPosition = FormStartPosition.CenterScreen };
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var label = new Label { Left = 16, Top = 12, Width = 510, Height = 42,
            Text = "Running as " + identity.Name + "\nAdd a task, check it off, then clear completed tasks." };
        var input = new TextBox { Left = 16, Top = 64, Width = 370 };
        var add = new Button { Left = 398, Top = 62, Width = 130, Text = "Add task" };
        var items = new CheckedListBox { Left = 16, Top = 104, Width = 512, Height = 220, CheckOnClick = true };
        var clear = new Button { Left = 16, Top = 342, Width = 200, Text = "Clear completed tasks" };
        add.Click += delegate { if (!String.IsNullOrWhiteSpace(input.Text)) { items.Items.Add(input.Text.Trim()); input.Clear(); input.Focus(); } };
        clear.Click += delegate {
            for (var index = items.Items.Count - 1; index >= 0; index--) if (items.GetItemChecked(index)) items.Items.RemoveAt(index);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demo-result.txt"), "Clear completed ran. Remaining tasks: " + items.Items.Count);
        };
        form.Controls.AddRange(new Control[] { label, input, add, items, clear });
        form.AcceptButton = add;
        form.Shown += delegate {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "demo-ready.txt"),
                "Identity: " + identity.Name + "\nAdmin: " + principal.IsInRole(WindowsBuiltInRole.Administrator) + "\nPID: " + System.Diagnostics.Process.GetCurrentProcess().Id);
            input.Focus();
        };
        Application.Run(form);
    }
}
