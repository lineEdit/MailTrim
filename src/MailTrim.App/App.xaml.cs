using System.Windows;

namespace MailTrim.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\MailTrim-" + Environment.UserName, out var first);
        if (!first) { MessageBox.Show("MailTrim уже запущен."); Shutdown(); return; }
        try
        {
            var store = new LocalStore();
            var window = new MainWindow(store);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть локальные данные MailTrim. Проверьте права доступа и свободное место.\nКод: " + ex.GetType().Name, "MailTrim");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
