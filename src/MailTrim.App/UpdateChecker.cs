using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace MailTrim.App;

public static class UpdateChecker
{
    public static bool ValidRepository(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    public static async Task Check(Window owner, LocalStore store, bool silent, string? candidate = null)
    {
        var repo = candidate ?? store.Settings.UpdateRepository;
        if (!ValidRepository(repo)) { if (!silent) MessageBox.Show(owner, "Укажите GitHub-репозиторий в формате owner/repository."); return; }
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1_000_000 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MailTrim/" + typeof(UpdateChecker).Assembly.GetName().Version!.ToString(3));
            var json = await client.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
            var current = typeof(UpdateChecker).Assembly.GetName().Version!;
            if (!Version.TryParse(tag, out var latest)) throw new FormatException();
            if (latest > new Version(current.Major, current.Minor, current.Build))
            {
                if (MessageBox.Show(owner, $"Доступна версия {latest}. Открыть страницу релиза?", "Обновление MailTrim", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo($"https://github.com/{repo}/releases/latest") { UseShellExecute = true });
            }
            else if (!silent) MessageBox.Show(owner, "Установлена актуальная версия.", "MailTrim");
        }
        catch { store.Log("update_check_failed"); if (!silent) MessageBox.Show(owner, "Не удалось проверить версию: сеть недоступна, превышен лимит GitHub или нет стабильных релизов."); }
    }
}
