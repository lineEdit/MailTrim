using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailTrim.Core.Gmail;

namespace MailTrim.App;

public sealed class GmailOAuth : IDisposable
{
    private readonly GmailVault vault;
    private GmailCredentials? credentials;
    private readonly HttpClient http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim gate = new(1);
    public bool Connected => credentials?.RefreshToken.Length > 0;
    public GmailOAuth(GmailVault vault) { this.vault = vault; credentials = vault.Load<GmailCredentials>("credentials"); }
    public static (string Id, string Secret) ParseDesktopConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("installed", out var installed)) throw new FormatException("desktop_client_required");
        var id = GmailMime.String(installed,"client_id"); var secret = GmailMime.String(installed,"client_secret");
        ValidateConfig(id,secret); return (id,secret);
    }
    private static void ValidateConfig(string id, string secret)
    {
        if (id.Length is < 10 or > 500 || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || id.Any(char.IsWhiteSpace) || secret.Length is < 1 or > 500 || secret.Any(char.IsControl)) throw new FormatException("desktop_client_required");
    }
    public static (bool Valid, string? Code, string? Error) ParseCallback(string target, string expectedState)
    {
        if (target.Length > 8192 || !target.StartsWith("/oauth2callback?",StringComparison.Ordinal) || target.Contains('#')) return (false,null,null);
        var query = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var entry in target["/oauth2callback?".Length..].Split('&'))
        {
            var pair = entry.Split('=',2);
            if (pair.Length != 2 || !query.TryAdd(Uri.UnescapeDataString(pair[0]),Uri.UnescapeDataString(pair[1].Replace("+"," ")))) return (false,null,null);
        }
        if (!query.TryGetValue("state",out var received) || received.Length != expectedState.Length
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received),Encoding.UTF8.GetBytes(expectedState))) return (false,null,null);
        query.TryGetValue("code",out var code); query.TryGetValue("error",out var error);
        return (true,code,error);
    }
    public async Task Connect(string id, string secret, CancellationToken token)
    {
        ValidateConfig(id,secret);
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/oauth2callback";
            var state = GmailMime.Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = GmailMime.Base64Url(RandomNumberGenerator.GetBytes(48));
            string E(string text) => Uri.EscapeDataString(text);
            var auth = "https://accounts.google.com/o/oauth2/v2/auth?client_id=" + E(id) + "&redirect_uri=" + E(redirect)
                + "&response_type=code&scope=" + E("https://www.googleapis.com/auth/gmail.modify")
                + "&access_type=offline&prompt=consent&state=" + E(state) + "&code_challenge_method=S256&code_challenge=" + GmailMime.PkceChallenge(verifier);
            Process.Start(new ProcessStartInfo(auth) { UseShellExecute = true });
            string? code = null;
            for (int attempt = 0; attempt < 10 && code is null; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = socket.GetStream(); var buffer = new byte[8192]; int length = 0;
                while (length < buffer.Length)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(length), readTimeout.Token); if (count == 0) break; length += count;
                    if (Encoding.ASCII.GetString(buffer,0,length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var line = Encoding.ASCII.GetString(buffer,0,length).Split("\r\n")[0].Split(' ');
                bool valid = false; string? error = null;
                if (line.Length == 3 && line[0] == "GET" && line[1].StartsWith("/oauth2callback?", StringComparison.Ordinal))
                {
                    var callback = ParseCallback(line[1],state);
                    valid = callback.Valid; code = callback.Code; error = callback.Error;
                }
                var message = valid && code is { Length: > 0 } ? "You can close this window and return to MailTrim." : "MailTrim: sign-in was not completed.";
                var body = Encoding.UTF8.GetBytes(message);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
                await stream.WriteAsync(header, readTimeout.Token); await stream.WriteAsync(body, readTimeout.Token);
                if (error is not null) throw new GmailApiException("gmail_consent_cancelled");
            }
            if (string.IsNullOrEmpty(code)) throw new GmailApiException("gmail_sign_in");
            var response = await Exchange(new() { ["client_id"] = id, ["client_secret"] = secret, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code" }, timeout.Token);
            if (response.RefreshToken.Length == 0) throw new GmailApiException("gmail_sign_in");
            vault.ClearCache(); // A different Google account must never inherit the old account's cache.
            credentials = response with { ClientId = id, ClientSecret = secret }; vault.Save("credentials",credentials);
        }
        finally { gate.Release(); }
    }
    private async Task<GmailCredentials> Exchange(Dictionary<string,string> fields, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30)); token = timeout.Token;
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", content, token);
        if (!response.IsSuccessStatusCode) throw new GmailApiException("gmail_sign_in");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var dataBuffer = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer,token)) > 0)
        { if (dataBuffer.Length + count > 65536) throw new GmailApiException("gmail_sign_in"); dataBuffer.Write(buffer,0,count); }
        var bytes = dataBuffer.ToArray();
        using var data = JsonDocument.Parse(bytes); var root = data.RootElement;
        var access = GmailMime.String(root,"access_token");
        if (access.Length == 0) throw new GmailApiException("gmail_sign_in");
        return new("","", GmailMime.String(root,"refresh_token"), access, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(root.GetProperty("expires_in").GetInt32(),60,86400)));
    }
    public async Task<string> AccessToken(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (credentials is not { } current || current.RefreshToken.Length == 0) throw new GmailApiException("gmail_sign_in");
            if (current.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var next = await Exchange(new() { ["client_id"] = current.ClientId, ["client_secret"] = current.ClientSecret, ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token" }, token);
                credentials = current with { AccessToken = next.AccessToken, ExpiresAt = next.ExpiresAt, RefreshToken = next.RefreshToken.Length == 0 ? current.RefreshToken : next.RefreshToken };
                vault.Save("credentials",credentials);
            }
            return credentials.AccessToken;
        }
        finally { gate.Release(); }
    }
    public async Task Disconnect()
    { await gate.WaitAsync(); try { vault.Clear(); credentials = null; } finally { gate.Release(); } }
    public void Dispose() => http.Dispose();
}
