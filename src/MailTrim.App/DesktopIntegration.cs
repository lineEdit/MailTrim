using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MailTrim.App;

public static class WindowsStartup
{
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("MailTrim", "\"" + Environment.ProcessPath + "\" --tray");
        else key.DeleteValue("MailTrim", false);
    }
}

public sealed class DesktopIntegration : IDisposable
{
    private readonly Window window;
    private readonly LocalStore store;
    private readonly Forms.NotifyIcon icon;
    public bool Exiting { get; private set; }
    public event Action? CheckRequested;
    private bool explained;
    public DesktopIntegration(Window window, LocalStore store, bool showTips = true)
    {
        this.window = window; this.store = store;
        icon = new Forms.NotifyIcon { Text = "MailTrim", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть MailTrim", null, (_, _) => Restore());
        menu.Items.Add("Проверить почту сейчас", null, (_, _) => CheckRequested?.Invoke());
        var quiet = new Forms.ToolStripMenuItem("Уведомления") { Checked = store.Settings.NotifyNewMail, CheckOnClick = true };
        quiet.CheckedChanged += (_, _) => { store.Settings.NotifyNewMail = quiet.Checked; store.Save(); };
        menu.Opening += (_, _) => quiet.Checked = store.Settings.NotifyNewMail;
        menu.Items.Add(quiet); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выйти", null, (_, _) => { Exiting = true; window.Close(); });
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => Restore(); icon.BalloonTipClicked += (_, _) => Restore();
        window.Closing += (_, e) =>
        {
            if (e.Cancel || Exiting || !store.Settings.CloseToTray) return;
            e.Cancel = true; window.Hide();
            if (!explained && showTips) { explained = true; icon.ShowBalloonTip(3000, "MailTrim работает в трее", "Двойной щелчок — открыть. «Выйти» в меню значка — завершить работу.", Forms.ToolTipIcon.Info); }
        };
        Application.Current.SessionEnding += (_, _) => Exiting = true;
    }
    public void Restore()
    {
        window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
    public void UpdateCounts(IEnumerable<MailTrim.Core.AccountProfile> profiles)
    {
        var entries = profiles.ToArray();
        bool known = entries.Length > 0 && entries.All(p => p.Status.Unread.HasValue);
        long total = entries.Sum(p => (long)(p.Status.Unread ?? 0));
        string label = !known ? "?" : total == 0 ? "M" : total > 99 ? "99+" : total.ToString();
        using var bitmap = new System.Drawing.Bitmap(64,64);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.FillEllipse(known && total > 0 ? System.Drawing.Brushes.RoyalBlue : System.Drawing.Brushes.SlateGray, 0,0,64,64);
        using var font = new System.Drawing.Font("Segoe UI", label.Length > 2 ? 24 : 32, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        using var format = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
        graphics.DrawString(label, font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0,0,64,64), format);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            var previous = icon.Icon; icon.Icon = (System.Drawing.Icon)borrowed.Clone(); previous?.Dispose();
        }
        finally { DestroyIcon(handle); }
        icon.Text = known ? $"MailTrim · непрочитано во входящих: {total}" : "MailTrim · счётчик неполон, откройте окно для подробностей";
    }    public void Notify(int count)
    {
        icon.Text = "MailTrim · новые письма";
        icon.ShowBalloonTip(5000, "Новые письма", $"Новых сообщений: {count}. Откройте MailTrim для чтения.", Forms.ToolTipIcon.Info);
    }
    public void Dispose() { icon.Visible = false; icon.Icon?.Dispose(); icon.ContextMenuStrip?.Dispose(); icon.Dispose(); }
}



