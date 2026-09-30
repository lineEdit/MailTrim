using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MailTrim.Core;

namespace MailTrim.App;

public static class UpdateInstaller
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailTrim", "Updates");
    private sealed record Plan(int ProcessId, string Previous, string Version);
    private static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Cannot start application");
    }
    public static bool Redirect(string[] args)
    {
        if (args.Contains("--skip-update") || args.Contains("--update-ready")) return false;
        try
        {
            var id = Guid.Parse(File.ReadAllText(Path.Combine(Root, "current.txt")));
            var executable = Path.Combine(Root, id.ToString("N"), "app", "MailTrim.exe");
            if (AssemblyName.GetAssemblyName(Path.ChangeExtension(executable, ".dll")).Version! <= typeof(App).Assembly.GetName().Version!) return false;
            using var child = Start(executable, args);
            return true;
        }
        catch { return false; }
    }
    public static void SignalReady(string[] args)
    {
        var index = Array.IndexOf(args, "--update-ready");
        if (index < 0 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out var id)) return;
        try { using var signal = EventWaitHandle.OpenExisting(@"Local\MailTrim-Update-" + id.ToString("N")); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }
    public static async Task Apply(string idText)
    {
        Process? child = null;
        Plan? plan = null;
        var oldExited = false;
        try
        {
            var id = Guid.ParseExact(idText, "N");
            var directory = Path.Combine(Root, id.ToString("N"));
            plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(directory, "plan.json"))) ?? throw new IOException();
            if (!string.Equals(plan.Previous, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected source");
            var executable = Path.Combine(directory, "app", "MailTrim.exe");
            if (AssemblyName.GetAssemblyName(Path.ChangeExtension(executable, ".dll")).Version != Version.Parse(plan.Version)) throw new IOException("Unexpected version");
            try
            {
                using var previous = Process.GetProcessById(plan.ProcessId);
                if (!string.Equals(previous.MainModule?.FileName, plan.Previous, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected process");
                await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            catch (ArgumentException) { /* The original process has already exited. */ }
            oldExited = true;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\MailTrim-Update-" + id.ToString("N"));
            child = Start(executable, "--update-ready", id.ToString("N"));
            if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(60))) || child.HasExited) throw new IOException("Startup failed");
            var pointer = Path.Combine(Root, "current.txt");
            File.WriteAllText(pointer + ".tmp", id.ToString("N"));
            File.Move(pointer + ".tmp", pointer, true);
        }
        catch
        {
            if (oldExited && plan is not null)
            {
                try
                {
                    if (child is { HasExited: false }) { child.Kill(); await child.WaitForExitAsync(); }
                    using var restored = Start(plan.Previous, "--skip-update");
                }
                catch { /* The original executable remains available for a manual restart. */ }
            }
            MessageBox.Show("Обновление не завершено. Предыдущая версия сохранена. Повторите попытку позже.", "MailTrim");
        }
        finally { child?.Dispose(); }
    }
    public static void Show(Window owner, LocalStore store, ReleaseCandidate release)
    {
        var window = new Window { Owner = owner, Title = "Обновление MailTrim", Width = 470, Height = 260, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var status = new TextBlock { Text = $"Версия {release.Tag}\nСкачать и установить обновление? Ящики и кэш сохранятся.", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Height = 8, Margin = new Thickness(0, 16, 0, 16), Maximum = 100 };
        var install = new Button { Content = "Скачать и установить", Margin = new Thickness(0, 0, 0, 8) };
        var cancel = new Button { Content = "Отмена" };
        using var cancellation = new CancellationTokenSource();
        var working = false;
        cancel.Click += (_, _) => { if (working) cancellation.Cancel(); else window.Close(); };
        window.Closing += (_, e) => { if (working) { e.Cancel = true; cancellation.Cancel(); } };
        install.Click += async (_, _) =>
        {
            working = true; install.IsEnabled = false;
            try
            {
                var id = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(Root, id);
                Directory.CreateDirectory(directory);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MailTrim/" + typeof(App).Assembly.GetName().Version!.ToString(3));
                var name = $"MailTrim-{release.Version.ToString(3)}-win-x64.zip";
                var url = $"https://github.com/lineEdit/MailTrim/releases/download/{Uri.EscapeDataString(release.Tag)}/{name}";
                status.Text = "Загрузка обновления…";
                var hashFile = Path.Combine(directory, "checksum.txt");
                await Download(client, url + ".sha256", hashFile, 4096, null, cancellation.Token);
                var hash = File.ReadAllText(hashFile).Trim('\uFEFF', ' ', '\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                var archive = Path.Combine(directory, "package.zip");
                await Download(client, url, archive, 400_000_000, new Progress<double>(value => progress.Value = value), cancellation.Token);
                status.Text = "Проверка и подготовка файлов…";
                var app = Path.Combine(directory, "app");
                await Task.Run(() => UpdatePackage.Extract(archive, hash, app, cancellation.Token), cancellation.Token);
                if (AssemblyName.GetAssemblyName(Path.Combine(app, "MailTrim.dll")).Version != release.Version) throw new InvalidDataException();
                cancellation.Token.ThrowIfCancellationRequested();
                if (MessageBox.Show(window, "Обновление готово. Сохраните незавершённые письма. Перезапустить MailTrim сейчас?", "MailTrim", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                File.WriteAllText(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(new Plan(Environment.ProcessId, Environment.ProcessPath!, release.Version.ToString())));
                using var helper = Start(Environment.ProcessPath!, "--apply-update", id);
                working = false;
                window.Close();
                System.Windows.Application.Current.Shutdown();
            }
            catch (OperationCanceledException) { status.Text = "Загрузка отменена. Приложение не изменено."; }
            catch { store.Log("update_install_failed"); status.Text = "Не удалось подготовить обновление. Проверьте сеть и свободное место. Приложение не изменено."; }
            finally { working = false; install.IsEnabled = !cancellation.IsCancellationRequested; cancel.Content = "Закрыть"; }
        };
        panel.Children.Add(status); panel.Children.Add(progress); panel.Children.Add(install); panel.Children.Add(cancel);
        window.Content = panel; window.ShowDialog();
    }
    private static async Task Download(HttpClient client, string url, string path, long limit, IProgress<double>? progress, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage!.RequestUri!;
        if (final.Scheme != "https" || !(final.Host == "github.com" || final.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))) throw new IOException("Unexpected download origin");
        var length = response.Content.Headers.ContentLength;
        if (length > limit) throw new IOException("Download too large");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read; if (total > limit) throw new IOException("Download too large");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (length > 0) progress?.Report(total * 100d / length.Value);
        }
    }
}
