using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace MailTrim.Core;

public sealed record UpdatePlan(int ProcessId, string Previous, string Version);
public enum UpdateResult { Installed, Rejected, PreviousStillRunning, RolledBack, ManualRestartRequired }

// No settings, mail profiles or cache are accessed during the handoff.
[SupportedOSPlatform("windows")]
public static class UpdateHandoff
{
    public static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Cannot start application");
    }

    public static async Task<UpdateResult> Apply(string root, string idText, string sourceExecutable,
        TimeSpan? exitTimeout = null, TimeSpan? startupTimeout = null)
    {
        Process? child = null;
        UpdatePlan? plan = null;
        var oldExited = false;
        try
        {
            var id = Guid.ParseExact(idText, "N");
            var directory = Path.Combine(root, id.ToString("N"));
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(directory, "plan.json"))) ?? throw new IOException();
            if (!string.Equals(plan.Previous, sourceExecutable, StringComparison.OrdinalIgnoreCase)) return UpdateResult.Rejected;
            var executable = Path.Combine(directory, "app", "MailTrim.exe");
            if (AssemblyName.GetAssemblyName(Path.ChangeExtension(executable, ".dll")).Version != Version.Parse(plan.Version)) return UpdateResult.Rejected;
            try
            {
                using var previous = Process.GetProcessById(plan.ProcessId);
                if (!string.Equals(previous.MainModule?.FileName, plan.Previous, StringComparison.OrdinalIgnoreCase)) return UpdateResult.Rejected;
                try { await previous.WaitForExitAsync().WaitAsync(exitTimeout ?? TimeSpan.FromSeconds(45)); }
                catch (TimeoutException) { return UpdateResult.PreviousStillRunning; }
            }
            catch (ArgumentException) { /* The original process has already exited. */ }
            oldExited = true;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\MailTrim-Update-" + id.ToString("N"));
            child = Start(executable, "--update-ready", id.ToString("N"));
            using var waitCancellation = new CancellationTokenSource();
            var readyTask = Task.Run(() => WaitHandle.WaitAny([ready, waitCancellation.Token.WaitHandle], startupTimeout ?? TimeSpan.FromSeconds(60)) == 0);
            var exitedTask = child.WaitForExitAsync();
            try
            {
                if (await Task.WhenAny(readyTask, exitedTask) != readyTask || !await readyTask) throw new IOException("Startup failed");
                // A ready signal followed immediately by a crash must not become the default version.
                if (await Task.WhenAny(exitedTask, Task.Delay(750)) == exitedTask) throw new IOException("Startup failed");
            }
            finally { waitCancellation.Cancel(); await readyTask; }
            var pointer = Path.Combine(root, "current.txt");
            var temporary = Path.Combine(root, id.ToString("N") + ".current.tmp");
            File.WriteAllText(temporary, id.ToString("N"));
            File.Move(temporary, pointer, true);
            return UpdateResult.Installed;
        }
        catch
        {
            if (!oldExited || plan is null) return UpdateResult.Rejected;
            try
            {
                if (child is { HasExited: false })
                {
                    child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                using var restored = Start(plan.Previous, "--skip-update");
                return UpdateResult.RolledBack;
            }
            catch { return UpdateResult.ManualRestartRequired; }
        }
        finally { child?.Dispose(); }
    }
}
