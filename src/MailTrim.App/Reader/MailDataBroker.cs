using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MailTrim.Core;

namespace MailTrim.App;

/// <summary>
/// A small local Go process that normalizes already extracted reader models.
/// It receives no browser profile, cookies, credentials, OAuth tokens or HTML.
/// </summary>
public sealed class MailDataBroker : IDisposable
{
    public static MailDataBroker Shared { get; } = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private long nextId;
    private bool unavailable;
    private bool disposed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<List<ReaderLetter>> CleanLetters(IEnumerable<ReaderLetter>? input, CancellationToken token)
    {
        var raw = (input ?? []).Take(50_000).ToList();
        var answer = await Send(new BrokerRequest((ulong)Interlocked.Increment(ref nextId), "clean_letters", raw, null), token);
        return answer?.Letters ?? raw.Select(ReaderData.Clean).OfType<ReaderLetter>().ToList();
    }

    public async Task<List<ReaderBlock>> CleanBlocks(IEnumerable<ReaderBlock>? input, CancellationToken token)
    {
        var raw = (input ?? []).Take(300).ToList();
        var answer = await Send(new BrokerRequest((ulong)Interlocked.Increment(ref nextId), "clean_blocks", null, raw), token);
        return answer?.Blocks ?? ReaderData.Clean(raw);
    }

    private async Task<BrokerResponse?> Send(BrokerRequest request, CancellationToken token)
    {
        if (disposed || unavailable) return null;
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!EnsureProcess()) return null;
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            var line = await process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            if (line is null) { Stop(); return null; }
            var answer = JsonSerializer.Deserialize<BrokerResponse>(line, Json);
            if (answer is not { OK: true } || answer.ID != request.ID) { Stop(); return null; }
            return answer;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            Stop(); return null;
        }
        finally { gate.Release(); }
    }

    private bool EnsureProcess()
    {
        if (process is { HasExited: false }) return true;
        Stop();
        var path = Path.Combine(AppContext.BaseDirectory, "runtime", "MailTrim.Driver.exe");
        if (!File.Exists(path)) { unavailable = true; return false; }
        try
        {
            process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            unavailable = true; process = null; return false;
        }
    }

    private void Stop()
    {
        var old = process; process = null;
        if (old is null) return;
        try { old.StandardInput.Close(); if (!old.WaitForExit(250)) old.Kill(true); } catch { }
        old.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); gate.Dispose();
    }

    private sealed record BrokerRequest(ulong ID, string Op, List<ReaderLetter>? Letters, List<ReaderBlock>? Blocks);
    private sealed record BrokerResponse(ulong ID, bool OK, List<ReaderLetter>? Letters, List<ReaderBlock>? Blocks, string? Error);
}
