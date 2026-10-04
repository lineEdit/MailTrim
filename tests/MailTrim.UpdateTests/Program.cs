using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MailTrim.Core;

if (args.Length == 3 && args[0] == "--verify-package")
{
    var checksum = File.ReadAllText(args[1] + ".sha256").Trim('\uFEFF', ' ', '\r', '\n').Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    UpdatePackage.Extract(args[1], checksum, args[2], default);
    if (AssemblyName.GetAssemblyName(Path.Combine(args[2], "MailTrim.dll")).Version != Assembly.GetExecutingAssembly().GetName().Version)
        throw new InvalidDataException("Published package version mismatch");
    Console.WriteLine("PASS actual published ZIP checksum, extraction and version");
    var packageRoot = Path.GetFullPath(args[2]);
    var kind = Path.GetFileName(args[1]).EndsWith("-lite.zip", StringComparison.Ordinal) ? DistributionKind.Lite : DistributionKind.Standalone;
    if (Path.GetFileName(args[1]) != ReleasePackage.Name(Assembly.GetExecutingAssembly().GetName().Version!, kind))
        throw new InvalidDataException("Unexpected package name");
    using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "MailTrim.runtimeconfig.json")));
    var requiresRuntime = configuration.RootElement.GetProperty("runtimeOptions").TryGetProperty("frameworks", out var frameworks);
    if (requiresRuntime != (kind == DistributionKind.Lite) || requiresRuntime && !frameworks.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "Microsoft.WindowsDesktop.App"))
        throw new InvalidDataException("Package runtime requirement mismatch");
    var probeRoot = Path.Combine(packageRoot, "launch-verification");
    using var application = UpdateHandoff.Start(Path.Combine(packageRoot, "MailTrim.exe"), "--verify-package-launch", probeRoot);
    try { await application.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
    catch (TimeoutException) { application.Kill(); await application.WaitForExitAsync(); throw; }
    var expectedReport = "PASS " + Assembly.GetExecutingAssembly().GetName().Version + " WPF WebView2 " + kind;
    if (application.ExitCode != 0 || File.ReadAllText(Path.Combine(probeRoot, "package-launch.txt")) != expectedReport)
        throw new InvalidDataException("Published application launch failed");
    Console.WriteLine($"PASS actual {kind} EXE starts WPF and bundled WebView2 using isolated offline data; update edition matches package"); return;
}

// Child processes only touch their isolated fixture folder, never MailTrim user data.
if (args.Length > 0 && args[0] == "--hold")
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "started.txt"), "ready");
    await Task.Delay(int.Parse(args[1])); return;
}
if (args.Length > 0 && args[0] == "--skip-update")
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rollback.txt"), "restored"); return;
}
if (args.Length > 0 && args[0] == "--update-ready")
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "child-pid.txt"), Environment.ProcessId.ToString());
    var behavior = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "behavior.txt"));
    if (behavior == "crash") return;
    if (behavior != "hang")
    {
        using var ready = EventWaitHandle.OpenExisting(@"Local\MailTrim-Update-" + args[1]); ready.Set();
        if (behavior == "ready-crash") return;
    }
    await Task.Delay(30000); return;
}

var root = Path.GetFullPath(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "MailTrim-update-tests-" + Guid.NewGuid().ToString("N")) : args[0]);
if (Directory.Exists(root)) throw new IOException("Tests require a new scratch directory");
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); passed++; }
async Task WaitFile(string path)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(path)) await Task.Delay(25, timeout.Token);
}
void CopyRunner(string destination, bool target)
{
    Directory.CreateDirectory(destination);
    var assembly = Assembly.GetExecutingAssembly().GetName().Name!;
    foreach (var name in new[] { assembly + ".exe", assembly + ".dll", assembly + ".deps.json", assembly + ".runtimeconfig.json", "MailTrim.Core.dll" })
        File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(destination, name));
    if (target)
    {
        File.Copy(Path.Combine(destination, assembly + ".exe"), Path.Combine(destination, "MailTrim.exe"));
        File.Copy(Path.Combine(destination, assembly + ".dll"), Path.Combine(destination, "MailTrim.dll"));
    }
}

foreach (var scenario in new[] { "ready", "crash", "hang", "ready-crash", "old-busy", "wrong-version", "wrong-process" })
{
    var folder = Path.Combine(root, scenario);
    var updates = Path.Combine(folder, "Updates");
    var id = Guid.NewGuid().ToString("N");
    var app = Path.Combine(updates, id, "app");
    var old = Path.Combine(folder, "old");
    CopyRunner(old, false); CopyRunner(app, true);
    var previousExe = Path.Combine(old, "MailTrim.UpdateTests.exe");
    File.WriteAllText(Path.Combine(app, "behavior.txt"), scenario);
    var originalPointer = Guid.NewGuid().ToString("N");
    File.WriteAllText(Path.Combine(updates, "current.txt"), originalPointer);
    // Sentinel bytes stand in for opaque profile/cache data; no live mailbox is opened.
    var sentinel = Path.Combine(folder, "user-data.bin");
    var sentinelBytes = new byte[] { 0, 19, 255, 61 }; File.WriteAllBytes(sentinel, sentinelBytes);
    using var previous = UpdateHandoff.Start(previousExe, "--hold", scenario == "old-busy" ? "30000" : "1200");
    try
    {
        await WaitFile(Path.Combine(old, "started.txt"));
        var plan = new UpdatePlan(scenario == "wrong-process" ? Environment.ProcessId : previous.Id, previousExe,
            scenario == "wrong-version" ? "99.0.0.0" : Assembly.GetExecutingAssembly().GetName().Version!.ToString());
        File.WriteAllText(Path.Combine(updates, id, "plan.json"), JsonSerializer.Serialize(plan));
        var result = await UpdateHandoff.Apply(updates, id, previousExe, TimeSpan.FromMilliseconds(1800), TimeSpan.FromSeconds(3));
        var expected = scenario switch
        {
            "ready" => UpdateResult.Installed,
            "old-busy" => UpdateResult.PreviousStillRunning,
            "wrong-version" or "wrong-process" => UpdateResult.Rejected,
            _ => UpdateResult.RolledBack
        };
        Check(result == expected, scenario + " handoff result");
        Check(File.ReadAllText(Path.Combine(updates, "current.txt")) == (scenario == "ready" ? id : originalPointer), scenario + " current version pointer");
        Check(File.ReadAllBytes(sentinel).SequenceEqual(sentinelBytes), scenario + " user data unchanged");
        Check(File.Exists(previousExe), scenario + " previous executable retained");
        if (expected == UpdateResult.RolledBack)
        {
            await WaitFile(Path.Combine(old, "rollback.txt"));
            Check(true, scenario + " previous process restarted");
        }
        if (scenario == "old-busy") Check(!previous.HasExited, "busy original process is not killed");
    }
    finally
    {
        if (!previous.HasExited) { previous.Kill(); await previous.WaitForExitAsync(); }
        var pidFile = Path.Combine(app, "child-pid.txt");
        if (File.Exists(pidFile))
        {
            try
            {
                using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                if (string.Equals(child.MainModule?.FileName, Path.Combine(app, "MailTrim.exe"), StringComparison.OrdinalIgnoreCase))
                { child.Kill(); await child.WaitForExitAsync(); }
            }
            catch (ArgumentException) { }
        }
    }
}
Console.WriteLine($"{passed} real-process update checks passed.");
