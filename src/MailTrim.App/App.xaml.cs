using System.Windows;

namespace MailTrim.App;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\MailTrim-" + Environment.UserName, out var first);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MailTrim-Activate-" + Environment.UserName);
        if (!first) { activation.Set(); Shutdown(); return; }
        try
        {
            var store = new LocalStore();
            if (store.Settings.StartWithWindows)
            {
                try { WindowsStartup.SetEnabled(true); } catch { store.Log("startup_registration_failed"); }
            }
            var window = new MainWindow(store, true);
            MainWindow = window;
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(() => window.RestoreWindow()), null, Timeout.Infinite, false);
            window.Show();
            if (e.Args.Contains("--tray")) window.Hide();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть локальные данные MailTrim. Проверьте права доступа и свободное место.\nКод: " + ex.GetType().Name, "MailTrim");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { activationWait?.Unregister(null); activation?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}



