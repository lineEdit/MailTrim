using System.ComponentModel;
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

/// <summary>Google Desktop flow: system browser, one-use loopback callback and PKCE.</summary>
public sealed class GmailOAuth : IDisposable
{
    private const string RequiredScope = "https://www.googleapis.com/auth/gmail.modify";
    private readonly GmailVault vault;
    private readonly HttpClient http;
    private readonly Action<Uri> openBrowser;
    private readonly SemaphoreSlim gate = new(1);
    private GmailCredentials? credentials;
    public bool Connected => credentials?.RefreshToken.Length > 0;
    public GmailClientRegistration? SavedRegistration => credentials is { } saved
        && new GmailClientRegistration(saved.ClientId,saved.ClientSecret) is { IsValid: true } registration ? registration : null;
    public GmailOAuth(GmailVault vault, HttpMessageHandler? testHandler = null, Action<Uri>? openBrowser = null)
    {
        this.vault = vault; credentials = vault.Load<GmailCredentials>("credentials");
        http = new(testHandler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        this.openBrowser = openBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
    }
    public static (string Id, string Secret) ParseDesktopConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("installed",out var installed)
            || installed.ValueKind != JsonValueKind.Object) throw new FormatException("desktop_client_required");
        var config = GmailClientRegistration.Create(GmailMime.String(installed,"client_id"),GmailMime.String(installed,"client_secret"));
        return (config.ClientId,config.ClientSecret);
    }
    public static (bool Valid, string? Code, string? Error) ParseCallback(string target, string expectedState)
    {
        if (target.Length > 8192 || expectedState.Length == 0 || !target.StartsWith("/oauth2callback?",StringComparison.Ordinal) || target.Contains('#')) return (false,null,null);
        var query = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var entry in target["/oauth2callback?".Length..].Split('&'))
        {
            var pair = entry.Split('=',2);
            if (pair.Length != 2 || !ValidEscapes(pair[0]) || !ValidEscapes(pair[1])) return (false,null,null);
            var key = Uri.UnescapeDataString(pair[0].Replace("+"," ")); var value = Uri.UnescapeDataString(pair[1].Replace("+"," "));
            if (key.Any(char.IsControl) || value.Any(char.IsControl) || !query.TryAdd(key,value)) return (false,null,null);
        }
        if (!query.TryGetValue("state",out var received) || received.Length != expectedState.Length
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received),Encoding.UTF8.GetBytes(expectedState))) return (false,null,null);
        query.TryGetValue("code",out var code); query.TryGetValue("error",out var error);
        if (query.ContainsKey("code") && query.ContainsKey("error")) return (false,null,null);
        if ((code is { Length: > 0 and <= 4096 }) == (error is { Length: > 0 and <= 1000 })) return (false,null,null);
        return (true,code,error);
    }
    private static bool ValidEscapes(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '%' && (i + 2 >= text.Length || !Uri.IsHexDigit(text[++i]) || !Uri.IsHexDigit(text[++i]))) return false;
        return true;
    }
    public Task Connect(string id, string secret, CancellationToken token) => Connect(GmailClientRegistration.Create(id,secret),token);
    public async Task Connect(GmailClientRegistration config, CancellationToken token)
    {
        if (!config.IsValid) throw new GmailApiException("gmail_client_invalid");
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/oauth2callback";
            var state = GmailMime.Base64Url(RandomNumberGenerator.GetBytes(32)); var verifier = GmailMime.Base64Url(RandomNumberGenerator.GetBytes(48));
            string E(string text) => Uri.EscapeDataString(text);
            var auth = "https://accounts.google.com/o/oauth2/v2/auth?client_id=" + E(config.ClientId) + "&redirect_uri=" + E(redirect)
                + "&response_type=code&scope=" + E(RequiredScope)
                + "&access_type=offline&prompt=select_account%20consent&state=" + E(state) + "&code_challenge_method=S256&code_challenge=" + GmailMime.PkceChallenge(verifier);
            try { openBrowser(new Uri(auth)); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            { throw new GmailApiException("gmail_browser_unavailable"); }
            for (int attempt = 0; attempt < 32; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token); var stream = socket.GetStream(); string? target;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
                {
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    try { target = await ReadTarget(stream,port,readTimeout.Token); }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { continue; }
                    catch (IOException) { continue; }
                }
                var callback = target is null ? (Valid: false,Code: (string?)null,Error: (string?)null) : ParseCallback(target,state);
                if (!callback.Valid) { await CompleteBrowser(stream,false); continue; }
                if (callback.Error is not null)
                {
                    await CompleteBrowser(stream,false);
                    throw new GmailApiException(callback.Error == "access_denied" ? "gmail_consent_cancelled" : "gmail_sign_in");
                }
                try
                {
                    var response = await Exchange(new()
                    {
                        ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["code"] = callback.Code!,
                        ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
                    },timeout.Token);
                    if (response.RefreshToken.Length == 0) throw new GmailApiException("gmail_sign_in");
                    var next = response with { ClientId = config.ClientId, ClientSecret = config.ClientSecret };
                    // A different Google account must never inherit the old account's cache.
                    vault.ClearCache(); vault.Save("credentials",next); credentials = next;
                }
                catch { await CompleteBrowser(stream,false); throw; }
                // Confirmation follows token exchange and durable Windows-encrypted storage.
                await CompleteBrowser(stream,true); return;
            }
            throw new GmailApiException("gmail_sign_in");
        }
        finally { gate.Release(); }
    }
    private static async Task<string?> ReadTarget(NetworkStream stream, int port, CancellationToken token)
    {
        var buffer = new byte[8192]; int length = 0; string text = "";
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(length),token); if (count == 0) break; length += count;
            text = Encoding.ASCII.GetString(buffer,0,length);
            if (text.Contains("\r\n\r\n",StringComparison.Ordinal)) break;
        }
        if (!text.Contains("\r\n\r\n",StringComparison.Ordinal)) return null;
        var lines = text.Split("\r\n"); var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] is not ("HTTP/1.0" or "HTTP/1.1")) return null;
        var hosts = lines.Where(x => x.StartsWith("Host:",StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosts.Length != 1 || hosts[0][5..].Trim() != $"127.0.0.1:{port}") return null;
        return request[1];
    }
    private static async Task CompleteBrowser(NetworkStream stream, bool success)
    {
        var title = success ? "Gmail подключён" : "Вход не завершён";
        var message = success ? "Можно закрыть эту вкладку и вернуться в MailTrim." : "Вернитесь в MailTrim. Там можно повторить вход и посмотреть причину ошибки.";
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=ru><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>MailTrim</title>"
            + "<style>body{font:16px system-ui;background:#f4f6fb;color:#17243d;margin:0;display:grid;place-items:center;min-height:100vh}main{background:white;border-radius:20px;padding:40px;max-width:480px;margin:20px}b{display:inline-block;background:#3265ed;color:white;padding:8px 14px;border-radius:10px}h1{font-size:26px}p{line-height:1.5}</style>"
            + "<main><b>M</b><h1>" + title + "</h1><p>" + message + "</p></main></html>");
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nPragma: no-cache\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'\r\n\r\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await stream.WriteAsync(header,timeout.Token); await stream.WriteAsync(body,timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }
    private async Task<GmailCredentials> Exchange(Dictionary<string,string> fields, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var request = new HttpRequestMessage(HttpMethod.Post,"https://oauth2.googleapis.com/token") { Content = content };
            using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if (response.Content.Headers.ContentLength > 65536) throw new GmailApiException("gmail_sign_in");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var dataBuffer = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer,timeout.Token)) > 0)
            { if (dataBuffer.Length + count > 65536) throw new GmailApiException("gmail_sign_in"); dataBuffer.Write(buffer,0,count); }
            using var data = JsonDocument.Parse(dataBuffer.ToArray()); var root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new GmailApiException("gmail_sign_in");
            if (!response.IsSuccessStatusCode) throw new GmailApiException(GmailMime.String(root,"error") switch
            {
                "invalid_client" or "unauthorized_client" or "redirect_uri_mismatch" => "gmail_client_invalid",
                "access_denied" or "insufficient_scope" => "gmail_permissions", _ => "gmail_sign_in"
            });
            var access = GmailMime.String(root,"access_token"); var type = GmailMime.String(root,"token_type"); var refresh = GmailMime.String(root,"refresh_token");
            if (access.Length is 0 or > 16384 || access.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
                || refresh.Length > 16384 || refresh.Any(char.IsControl) || (type.Length > 0 && !type.Equals("Bearer",StringComparison.OrdinalIgnoreCase))
                || !root.TryGetProperty("expires_in",out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds <= 0) throw new GmailApiException("gmail_sign_in");
            // Omitted scope means the requested scope was granted. An explicit subset does not.
            if (root.TryGetProperty("scope",out var scope))
            {
                if (scope.ValueKind != JsonValueKind.String || scope.GetString() is not { Length: <= 4096 } granted || granted.Any(char.IsControl))
                    throw new GmailApiException("gmail_sign_in");
                if (!granted.Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains(RequiredScope,StringComparer.Ordinal))
                    throw new GmailApiException("gmail_permissions");
            }
            return new("","",refresh,access,DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds,60,86400)));
        }
        catch (JsonException) { throw new GmailApiException("gmail_sign_in"); }
        catch (HttpRequestException) { throw new GmailApiException("gmail_network"); }
        catch (IOException) { throw new GmailApiException("gmail_network"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new GmailApiException("gmail_network"); }
    }
    public async Task<string> AccessToken(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (credentials is not { } current || current.RefreshToken.Length == 0) throw new GmailApiException("gmail_sign_in");
            if (current.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var next = await Exchange(new() { ["client_id"] = current.ClientId, ["client_secret"] = current.ClientSecret, ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token" },token);
                var saved = current with { AccessToken = next.AccessToken, ExpiresAt = next.ExpiresAt, RefreshToken = next.RefreshToken.Length == 0 ? current.RefreshToken : next.RefreshToken };
                vault.Save("credentials",saved); credentials = saved;
            }
            return credentials.AccessToken;
        }
        finally { gate.Release(); }
    }
    public async Task Disconnect()
    { await gate.WaitAsync(); try { vault.Clear(); credentials = null; } finally { gate.Release(); } }
    public void Dispose() => http.Dispose();
}
