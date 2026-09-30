using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using MailTrim.Core;

namespace MailTrim.App;

public static class UpdateChecker
{
    public static bool ValidRepository(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    public static async Task Check(Window owner, LocalStore store, bool silent, string? candidate = null, bool? includePrereleases = null)
    {
        var repo = candidate ?? store.Settings.UpdateRepository;
        var preview = includePrereleases ?? store.Settings.IncludePrereleaseUpdates;
        if (!ValidRepository(repo)) { if (!silent) MessageBox.Show(owner, "Укажите GitHub-репозиторий в формате owner/repository."); return; }
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1_000_000 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MailTrim/" + typeof(UpdateChecker).Assembly.GetName().Version!.ToString(3));
            var endpoint = preview ? "releases?per_page=100" : "releases/latest";
            using var response = await client.GetAsync($"https://api.github.com/repos/{repo}/{endpoint}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (!silent) MessageBox.Show(owner, "Релизы выбранного канала не найдены или репозиторий недоступен. Для текущих сборок MailTrim включите предварительные версии.", "Обновление MailTrim");
                return;
            }
            response.EnsureSuccessStatusCode();
            var latest = ReleaseCatalog.Select(await response.Content.ReadAsStringAsync(), preview);
            if (latest is null)
            {
                if (!silent) MessageBox.Show(owner, "В выбранном канале пока нет опубликованных версий с поддерживаемым номером.", "Обновление MailTrim");
                return;
            }
            var current = typeof(UpdateChecker).Assembly.GetName().Version!;
            if (latest.Version > current)
            {
                var channel = latest.Prerelease ? "предварительная" : "стабильная";
                if (repo.Equals("lineEdit/MailTrim", StringComparison.OrdinalIgnoreCase))
                    UpdateInstaller.Show(owner, store, latest);
                else if (MessageBox.Show(owner, $"Доступна {channel} версия {latest.Tag}. Установлена {current.ToString(3)}. Открыть страницу релиза?", "Обновление MailTrim", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo($"https://github.com/{repo}/releases/tag/{Uri.EscapeDataString(latest.Tag)}") { UseShellExecute = true });
            }
            else if (!silent) MessageBox.Show(owner, "Установлена актуальная версия.", "MailTrim");
        }
        catch { store.Log("update_check_failed"); if (!silent) MessageBox.Show(owner, "Не удалось проверить версию: проверьте сеть, адрес репозитория или повторите позже при ограничении запросов GitHub."); }
    }
}
