using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using MailTrim.App;
using MailTrim.Core;
using MailTrim.Core.Gmail;

namespace MailTrim.Smoke;

/// <summary>Real local callbacks with synthetic Google replies; never launches a browser or contacts Google.</summary>
internal static class GmailBrowserChecks
{
    private static readonly GmailClientRegistration Registration = new("synthetic-browser.apps.googleusercontent.com","synthetic-client-secret");
    private const string Tokens = "{\"access_token\":\"synthetic-access\",\"refresh_token\":\"synthetic-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"openid https://www.googleapis.com/auth/gmail.modify\"}";
    public static async Task Run(LocalStore store, Action<bool,string> check)
    {
        foreach (var json in new[] { "[]", "null", "0", "\"value\"", "{\"installed\":null}", "{\"installed\":[]}", "{\"installed\":5}", "{\"installed\":\"value\"}" })
        {
            bool rejected = false; try { GmailOAuth.ParseDesktopConfig(json); } catch (FormatException) { rejected = true; }
            check(rejected,"invalid Google registration JSON shape fails as a validation error");
        }
        var vault = new GmailVault(store.Root,Guid.NewGuid());
        vault.Save("cache",new GmailCache([],[],"old@example.test"));
        Dictionary<string,string>? authorization = null; Dictionary<string,string>? exchange = null;
        Task? browser = null; Uri? callback = null; string? completion = null; int posts = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using (var oauth = new GmailOAuth(vault,new Handler(async request =>
        {
            posts++;
            check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/token","browser login exchanges code only at the fixed Google token endpoint");
            exchange = Form(await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.OK,Tokens);
        }),uri =>
        {
            check(uri.Scheme == "https" && uri.Host == "accounts.google.com","browser login launches the official Google authorization page");
            authorization = Form(uri.Query.TrimStart('?'));
            browser = Visit();
            async Task Visit()
            {
                using var client = LocalClient();
                var redirect = new Uri(authorization["redirect_uri"]);
                check(redirect.Scheme == "http" && redirect.Host == "127.0.0.1" && redirect.Port > 0 && redirect.AbsolutePath == "/oauth2callback","browser login binds a temporary loopback callback rather than a remote service");
                var wrong = redirect.AbsoluteUri + "?state=wrong&code=wrong";
                using var rejected = await client.GetAsync(wrong,deadline.Token);
                check(rejected.StatusCode == HttpStatusCode.BadRequest && posts == 0,"wrong callback state is rejected without exchanging a code");
                callback = Callback(authorization,"code=synthetic%2Fcode");
                using var response = await client.GetAsync(callback,deadline.Token);
                completion = await response.Content.ReadAsStringAsync(deadline.Token);
                check(response.IsSuccessStatusCode && vault.Load<GmailCredentials>("credentials")?.RefreshToken == "synthetic-refresh","browser success is returned only after the encrypted credentials are saved");
                check(response.Headers.CacheControl?.NoStore == true && response.Headers.Contains("Content-Security-Policy") && response.Headers.Contains("Referrer-Policy"),"browser completion is private, script-free and cannot be embedded");
            }
        }))
        {
            await oauth.Connect(Registration,deadline.Token); await browser!;
            check(oauth.Connected && oauth.SavedRegistration == Registration,"browser login retains its client registration for simple reconnect");
            check(authorization!["client_id"] == Registration.ClientId && authorization["scope"] == "https://www.googleapis.com/auth/gmail.modify"
                && authorization["response_type"] == "code" && authorization["access_type"] == "offline"
                && authorization["code_challenge_method"] == "S256" && authorization["state"].Length >= 40,
                "browser login requests the required Gmail scope with offline access and a random state");
            check(exchange!["code"] == "synthetic/code" && exchange["client_id"] == Registration.ClientId
                && exchange["client_secret"] == Registration.ClientSecret && exchange["grant_type"] == "authorization_code"
                && GmailMime.PkceChallenge(exchange["code_verifier"]) == authorization["code_challenge"]
                && exchange["redirect_uri"] == authorization["redirect_uri"],"Google token exchange is bound to this browser attempt by PKCE and redirect URI");
            check(completion!.Contains("Gmail подключён") && !completion.Contains("synthetic") && !completion.Contains(authorization["state"]),"localized browser success page never reflects account credentials or authorization values");
            check(await oauth.AccessToken(deadline.Token) == "synthetic-access" && posts == 1,"fresh access token is reused without another browser or exchange");
            check(vault.Load<GmailCache>("cache") is null,"successful browser account switch removes the prior account's cached messages");
        }
        bool stopped = false;
        try { using var client = LocalClient(); using var ignored = await client.GetAsync(callback,deadline.Token); }
        catch (HttpRequestException) { stopped = true; }
        check(stopped && posts == 1,"finished browser callback is closed and cannot replay an authorization code");
        using (var reloaded = new GmailOAuth(vault,new Handler(_ => throw new Exception("Unexpected exchange")),_ => throw new Exception("Unexpected browser")))
            check(reloaded.Connected && reloaded.SavedRegistration == Registration,"restarting Gmail restores the saved client without asking for JSON again");

        await Failure(store,check,"consent cancellation","error=access_denied",HttpStatusCode.OK,Tokens,"gmail_consent_cancelled",0);
        await Failure(store,check,"invalid application registration","code=synthetic",HttpStatusCode.BadRequest,"{\"error\":\"invalid_client\"}","gmail_client_invalid",1);
        await Failure(store,check,"denied Gmail permissions","code=synthetic",HttpStatusCode.Forbidden,"{\"error\":\"access_denied\"}","gmail_permissions",1);
        await Failure(store,check,"partial Gmail permission grant","code=synthetic",HttpStatusCode.OK,"{\"access_token\":\"synthetic\",\"refresh_token\":\"synthetic\",\"expires_in\":3600,\"scope\":\"openid email\"}","gmail_permissions",1);
        await Failure(store,check,"oversized granted scope","code=synthetic",HttpStatusCode.OK,"{\"access_token\":\"synthetic\",\"refresh_token\":\"synthetic\",\"expires_in\":3600,\"scope\":\"" + new string('x',4097) + "\"}","gmail_sign_in",1);
        await Failure(store,check,"missing offline token","code=synthetic",HttpStatusCode.OK,"{\"access_token\":\"synthetic\",\"expires_in\":3600}","gmail_sign_in",1);
        await Failure(store,check,"malformed token response","code=synthetic",HttpStatusCode.OK,"not-json","gmail_sign_in",1);
        await Failure(store,check,"wrong token expiry type","code=synthetic",HttpStatusCode.OK,"{\"access_token\":\"synthetic\",\"refresh_token\":\"synthetic\",\"expires_in\":\"bad\"}","gmail_sign_in",1);
        await Failure(store,check,"invalid bearer token","code=synthetic",HttpStatusCode.OK,"{\"access_token\":\"synthetic\\r\\nheader\",\"refresh_token\":\"synthetic\",\"expires_in\":3600}","gmail_sign_in",1);

        var networkVault = new GmailVault(store.Root,Guid.NewGuid()); Task? networkBrowser = null; string? networkPage = null;
        using (var oauth = new GmailOAuth(networkVault,new Handler(_ => throw new HttpRequestException("synthetic network failure")),uri =>
        {
            networkBrowser = Visit();
            async Task Visit()
            {
                using var client = LocalClient(); using var response = await client.GetAsync(Callback(Form(uri.Query.TrimStart('?')),"code=synthetic"),deadline.Token);
                networkPage = await response.Content.ReadAsStringAsync(deadline.Token);
            }
        }))
        {
            string? error = null; try { await oauth.Connect(Registration,deadline.Token); } catch (GmailApiException ex) { error = ex.Message; }
            await networkBrowser!;
            check(error == "gmail_network" && !oauth.Connected && networkVault.Load<GmailCredentials>("credentials") is null && networkPage!.Contains("Вход не завершён"),
                "network failure cannot create a connected account or a false browser success page");
        }

        var cancelledVault = new GmailVault(store.Root,Guid.NewGuid()); Uri? cancelledRedirect = null;
        using (var cancel = new CancellationTokenSource())
        using (var oauth = new GmailOAuth(cancelledVault,new Handler(_ => throw new Exception("Unexpected exchange")),uri => { cancelledRedirect = new(Form(uri.Query.TrimStart('?'))["redirect_uri"]); cancel.Cancel(); }))
        {
            bool cancelled = false; try { await oauth.Connect(Registration,cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !oauth.Connected && cancelledVault.Load<GmailCredentials>("credentials") is null,"cancelled browser login stops waiting without saving tokens");
        }
        await Closed(cancelledRedirect!,check,"cancelling browser login releases its loopback port");

        Uri? failedRedirect = null;
        using (var oauth = new GmailOAuth(new(store.Root,Guid.NewGuid()),new Handler(_ => throw new Exception("Unexpected exchange")),uri =>
        { failedRedirect = new(Form(uri.Query.TrimStart('?'))["redirect_uri"]); throw new Win32Exception(); }))
        {
            string? error = null; try { await oauth.Connect(Registration,deadline.Token); } catch (GmailApiException ex) { error = ex.Message; }
            check(error == "gmail_browser_unavailable" && !oauth.Connected,"a missing system browser produces a specific error rather than hanging");
        }
        await Closed(failedRedirect!,check,"browser launch failure releases its loopback port");

        var refreshVault = new GmailVault(store.Root,Guid.NewGuid());
        refreshVault.Save("credentials",new GmailCredentials(Registration.ClientId,Registration.ClientSecret,"refresh-before","expired",DateTimeOffset.UtcNow.AddMinutes(-1)));
        using (var oauth = new GmailOAuth(refreshVault,new Handler(async request =>
        {
            var fields = Form(await request.Content!.ReadAsStringAsync());
            check(fields["grant_type"] == "refresh_token" && fields["refresh_token"] == "refresh-before" && fields["client_id"] == Registration.ClientId,
                "Gmail refresh uses the saved client's token without opening a browser");
            return Json(HttpStatusCode.OK,"{\"access_token\":\"refreshed\",\"expires_in\":3600}");
        }),_ => throw new Exception("Refresh must not launch a browser")))
        {
            check(await oauth.AccessToken(deadline.Token) == "refreshed" && refreshVault.Load<GmailCredentials>("credentials")?.RefreshToken == "refresh-before",
                "refresh updates the access token and preserves an omitted refresh token");
        }
        check(!GmailOAuth.ParseCallback("/oauth2callback?state=expected&code=x&error=access_denied","expected").Valid
            && !GmailOAuth.ParseCallback("/oauth2callback?state=expected&code=x%0a","expected").Valid
            && !GmailOAuth.ParseCallback("/oauth2callback?state=expected&code=%ZZ","expected").Valid,
            "ambiguous, control-character and malformed-escape callbacks are rejected");
    }
    private static async Task Failure(LocalStore store,Action<bool,string> check,string title,string result,HttpStatusCode status,string json,string expected,int expectedPosts)
    {
        var vault = new GmailVault(store.Root,Guid.NewGuid()); var prior = new GmailCredentials(Registration.ClientId,Registration.ClientSecret,"old-refresh","old-access",DateTimeOffset.UtcNow.AddHours(1));
        vault.Save("credentials",prior); vault.Save("cache",new GmailCache([],[],"old@example.test"));
        Task? browser = null; int posts = 0; string? page = null; HttpStatusCode? callbackStatus = null; string? error = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var oauth = new GmailOAuth(vault,new Handler(_ => { posts++; return Task.FromResult(Json(status,json)); }),uri =>
        {
            browser = Visit();
            async Task Visit()
            {
                using var client = LocalClient(); using var response = await client.GetAsync(Callback(Form(uri.Query.TrimStart('?')),result),deadline.Token);
                callbackStatus = response.StatusCode; page = await response.Content.ReadAsStringAsync(deadline.Token);
            }
        });
        try { await oauth.Connect(Registration,deadline.Token); } catch (GmailApiException ex) { error = ex.Message; }
        await browser!;
        check(error == expected && posts == expectedPosts && callbackStatus == HttpStatusCode.BadRequest && page!.Contains("Вход не завершён") && !page.Contains("Gmail подключён"),
            $"{title}: browser failure does not falsely confirm a connected Gmail");
        check(vault.Load<GmailCredentials>("credentials") == prior && vault.Load<GmailCache>("cache")?.Address == "old@example.test",
            $"{title}: failed reconnect preserves the existing account and its encrypted cache");
    }
    private static Task Closed(Uri uri,Action<bool,string> check,string title)
    {
        // Windows can silently discard a SYN to a closed local port. Exclusively rebinding
        // the exact callback port proves disposal without depending on refusal timing.
        using var replacement = new TcpListener(IPAddress.Loopback,uri.Port) { ExclusiveAddressUse = true };
        bool closed = false;
        try { replacement.Start(); closed = true; }
        catch (SocketException) { }
        check(closed,title);
        return Task.CompletedTask;
    }
    private static HttpClient LocalClient() => new(new HttpClientHandler { UseProxy = false,UseCookies = false,AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private static Uri Callback(Dictionary<string,string> authorization,string result) => new(authorization["redirect_uri"] + "?state=" + Uri.EscapeDataString(authorization["state"]) + "&" + result);
    private static Dictionary<string,string> Form(string value) => value.Split('&').Select(p => p.Split('=',2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace("+"," ")),p => Uri.UnescapeDataString(p[1].Replace("+"," ")),StringComparer.Ordinal);
    private static HttpResponseMessage Json(HttpStatusCode status,string json) => new(status) { Content = new StringContent(json,Encoding.UTF8,"application/json") };
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) { token.ThrowIfCancellationRequested(); return respond(request); }
    }
}
