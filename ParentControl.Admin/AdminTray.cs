using System.Drawing;
using Forms = System.Windows.Forms;

namespace ParentControl.Admin;

internal sealed class AdminTray : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon? appIcon;
    private readonly Forms.ToolStripMenuItem connected = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem queue = new() { Enabled = false };

    public AdminTray(Action open, Action exit)
    {
        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(connected);
        menu.Items.Add(queue);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Открыть Admin", null, (_, _) => open());
        menu.Items.Add("Выйти из Admin", null, (_, _) => exit());
        icon = new Forms.NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "ParentControl Admin",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => open();
    }

    public void Update(int online, int total, int pending)
    {
        connected.Text = $"В сети: {online} из {total}";
        queue.Text = $"Команд в очереди: {pending}";
        icon.Text = $"ParentControl: {online}/{total} в сети, очередь: {pending}";
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        appIcon?.Dispose();
    }
}
