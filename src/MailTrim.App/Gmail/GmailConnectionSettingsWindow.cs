using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MailTrim.App;

/// <summary>Optional, once-per-installation registration; never part of ordinary account sign-in.</summary>
public sealed class GmailConnectionSettingsWindow : Window
{
    public GmailConnectionSettingsWindow(GmailRegistrationStore registration)
    {
        Title = "Разовая настройка MailTrim в Google";
        Width = 560; MaxHeight = 720; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Регистрация приложения", FontSize = 24, Margin = new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = "Google требует зарегистрировать приложение, которое получает доступ к Gmail. Для готового выпуска это делает разработчик. Свой клиент можно настроить здесь один раз — он будет доступен всем новым вкладкам Gmail на этом компьютере.", TextWrapping = TextWrapping.Wrap });
        var help = new Button { Content = "Открыть пошаговую инструкцию", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,12) };
        help.Click += (_,_) => Open("https://github.com/lineEdit/MailTrim/blob/main/docs/GMAIL.md"); panel.Children.Add(help);
        var current = registration.Load();
        var id = new TextBox { Text = current?.ClientId ?? "", MaxLength = 500, Margin = new Thickness(0,4,0,12) };
        var secret = new PasswordBox { Password = current?.ClientSecret ?? "", MaxLength = 500, Margin = new Thickness(0,4,0,12) };
        panel.Children.Add(new TextBlock { Text = "Идентификатор приложения (Client ID)" }); panel.Children.Add(id);
        panel.Children.Add(new TextBlock { Text = "Ключ Desktop-клиента (Client secret)" }); panel.Children.Add(secret);
        panel.Children.Add(new TextBlock { Text = "Это параметры приложения из Google Cloud, а не пароль аккаунта Google.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var import = new Button { Content = "Загрузить настройки из файла Google…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,12) }; panel.Children.Add(import);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) }; panel.Children.Add(error);
        import.Click += (_,_) =>
        {
            var file = new OpenFileDialog { Filter = "Настройки Google (*.json)|*.json", Title = "Настройки Desktop-клиента Google" };
            if (file.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(file.FileName).Length > 65536) throw new FormatException();
                var pair = GmailOAuth.ParseDesktopConfig(File.ReadAllText(file.FileName));
                id.Text = pair.Id; secret.Password = pair.Secret;
                error.Text = "Настройки загружены. Нажмите «Сохранить».";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            { error.Text = "Не удалось прочитать настройки. Нужен клиент Google типа Desktop app."; }
        };
        var save = new Button { Content = "Сохранить", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_,_) =>
        {
            try { registration.Save(GmailClientRegistration.Create(id.Text.Trim(),secret.Password)); DialogResult = true; }
            catch (FormatException) { error.Text = "Проверьте Client ID и Client secret клиента типа Desktop app."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { error.Text = "Не удалось сохранить настройки на этом компьютере. Проверьте доступ к локальным файлам."; }
        };
        panel.Children.Add(save);
    }
    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox.Show(this,"Не удалось открыть браузер. Проверьте браузер по умолчанию в Windows.","MailTrim"); }
    }
}
