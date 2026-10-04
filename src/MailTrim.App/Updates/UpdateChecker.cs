using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using MailTrim.Core;

namespace MailTrim.App;

public static class UpdateChecker
{
    private static int checking;
    public static bool ValidRepository(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant);
    public static async Task Check(Window owner, LocalStore store, bool silent, string? candidate = null, bool? includePrereleases = null)
    {
        var repo = candidate ?? store.Settings.UpdateRepository;
        var preview = includePrereleases ?? store.Settings.IncludePrereleaseUpdates;
        if (!ValidRepository(repo)) { if (!silent) MessageBox.Show(owner, "Укажите GitHub-репозиторий в формате owner/repository."); return; }
        if (Interlocked.Exchange(ref checking, 1) != 0)
        {
            if (!silent) MessageBox.Show(owner, "Проверка обновлений уже выполняется.", "Обновление MailTrim");
            return;
        }
        try
        {
            using var client = UpdateCatalogClient.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MailTrim/" + typeof(UpdateChecker).Assembly.GetName().Version!.ToString(3));
            var endpoint = preview ? "releases?per_page=100" : "releases/latest";
            var latest = await UpdateCatalogClient.Fetch(client, new Uri($"https://api.github.com/repos/{repo}/{endpoint}"), preview);
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
        catch (UpdateCheckException ex)
        {
            store.Log("update_check_" + ex.Error.ToString().ToLowerInvariant());
            if (!silent)
            {
                var reason = ex.Error switch
                {
                    UpdateCheckError.RateLimit => "GitHub временно ограничил число запросов. Повторите проверку позже.",
                    UpdateCheckError.AccessDenied => "GitHub отказал в доступе к списку релизов.",
                    UpdateCheckError.NotFound => "Релизы выбранного канала не найдены. Проверьте репозиторий; для текущих сборок включите предварительные версии.",
                    UpdateCheckError.Timeout => "GitHub не ответил вовремя после двух попыток.",
                    UpdateCheckError.Network => "Не удалось соединиться с GitHub после двух попыток. Проверьте сеть или прокси.",
                    UpdateCheckError.Server => "GitHub временно недоступен после двух попыток.",
                    _ => "GitHub вернул неподдерживаемый ответ."
                };
                if (MessageBox.Show(owner, reason + "\n\nОткрыть страницу релизов в браузере для ручного обновления?", "Обновление MailTrim", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo($"https://github.com/{repo}/releases") { UseShellExecute = true }); }
                    catch { store.Log("update_browser_failed"); MessageBox.Show(owner, "Не удалось открыть браузер. Страница релизов: github.com/" + repo + "/releases", "Обновление MailTrim"); }
                }
            }
        }
        catch { store.Log("update_check_failed"); if (!silent) MessageBox.Show(owner, "Не удалось завершить проверку обновления.", "Обновление MailTrim"); }
        finally { Volatile.Write(ref checking, 0); }
    }
}
