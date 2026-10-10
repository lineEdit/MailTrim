using System.Net;
using System.Text;
using System.Text.Json;
using MailTrim.Core;
using MailTrim.Core.Gmail;

internal static class GmailChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        check(JsonSerializer.Deserialize<AccountProfile>("{\"name\":\"Old\"}",FilterRules.Json)!.Provider == MailProvider.MailRu,"old profiles remain Mail.ru");
        check(GmailMime.PkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM","OAuth PKCE matches RFC 7636 vector");
        var unicode = Encoding.UTF8.GetBytes("Текст ✓");
        check(GmailMime.Decode(GmailMime.Base64Url(unicode)).SequenceEqual(unicode),"Gmail UTF-8 base64url round trip");
        using var mime = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "safe", threadId = "thread", internalDate = "1760000000000", labelIds = new[] { "INBOX", "UNREAD" }, snippet = "Hi &amp; text",
            payload = new { mimeType = "multipart/alternative", headers = new[] { new { name = "From", value = "Sender <sender@example.test>" }, new { name = "Subject", value = "Тема" } },
                parts = new[] { new { mimeType = "text/plain", body = new { data = GmailMime.Base64Url(Encoding.UTF8.GetBytes("Native text")) } }, new { mimeType = "text/html", body = new { data = GmailMime.Base64Url(Encoding.UTF8.GetBytes("<script>malicious()</script><p>Fallback</p><img src='javascript:bad'><img src='https://example.test/photo.png'><img src='https://user:pass@example.test/x'>")) } } } }
        }));
        var message = GmailMime.Parse(mime.RootElement);
        check(message.Text == "Native text" && message.Subject == "Тема" && message.Unread,"Gmail prefers plain text and reads exact labels");
        check(message.Images.SequenceEqual(["https://example.test/photo.png"]),"Gmail only retains safe HTTPS images");
        check(GmailMime.HtmlText("<style>bad</style><p>Hello &amp; goodbye</p><script>bad()</script>") == "Hello & goodbye","HTML is inert text, no scripts or styles");
        var raw = Encoding.UTF8.GetString(GmailMime.Decode(GmailMime.RawMessage("Name <recipient@example.test>","Тема","Привет",message with { MessageId = "<source@example.test>" })));
        check(raw.Contains("To: recipient@example.test\r\n") && raw.Contains("In-Reply-To: <source@example.test>\r\n") && raw.Contains("charset=utf-8"),"compose produces MIME and preserves reply chain");
        bool rejected = false; try { GmailMime.RawMessage("x@example.test\r\nBcc: other@example.test","s","t"); } catch (FormatException) { rejected = true; }
        check(rejected,"compose rejects recipient header injection");
        rejected = false; try { GmailMime.RawMessage("x@example.test","subject\nBcc: x","t"); } catch (FormatException) { rejected = true; }
        check(rejected,"compose rejects subject header injection");
        var calls = new List<(string Path,string Method,string? Auth,string Body)>();
        using var api = new GmailClient(_ => Task.FromResult("synthetic-access"),new Handler(async request =>
        {
            var path = request.RequestUri!.PathAndQuery; var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            calls.Add((path,request.Method.Method,request.Headers.Authorization?.ToString(),body));
            string response;
            if (path.Contains("labels/INBOX")) response = "{\"messagesUnread\":7}";
            else if (path.Contains("messages?") && path.Contains("maxResults=100")) response = "{\"messages\":[{\"id\":\"new\"},{\"id\":\"old\"}]}";
            else if (path.Contains("messages?")) response = "{\"messages\":[{\"id\":\"older\"},{\"id\":\"newer\"}],\"nextPageToken\":\"next+token\"}";
            else if (path.Contains("attachments/")) response = "{\"data\":\"" + GmailMime.Base64Url(unicode) + "\"}";
            else response = JsonSerializer.Serialize(new { id = path.Contains("newer") ? "newer" : "older", threadId = "t", internalDate = path.Contains("newer") ? "1760000000000" : "1750000000000", labelIds = new[] { "INBOX" }, payload = new { mimeType = "text/plain", headers = Array.Empty<object>(), body = new { data = GmailMime.Base64Url(Encoding.UTF8.GetBytes("Body")) } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }));
        var status = await api.InboxStatus(default); check(status.Unread == 7 && status.Ids.SequenceEqual(["new","old"]),"Gmail unread count is server INBOX messagesUnread");
        var page = await api.Page("INBOX","is:unread & from:x", "page+token",default);
        check(page.Messages.Select(m => m.Id).SequenceEqual(["newer","older"]) && page.NextPageToken == "next+token","Gmail page uses exact dates and next token");
        check(calls.Any(c => c.Path.Contains("q=is%3Aunread%20%26%20from%3Ax") && c.Path.Contains("pageToken=page%2Btoken")),"Gmail query and paging parameters are escaped");
        _ = await api.Message("newer",default);
        check(calls.All(c => c.Method == "GET" && c.Auth == "Bearer synthetic-access"),"reading and preparing mail never mutate read status");
        check((await api.Attachment("newer","file",default)).SequenceEqual(unicode),"attachment data is decoded from API");
        await api.Modify("newer",[],["UNREAD"],default); await api.Trash("newer",default);
        check(calls.Any(c => c.Path.EndsWith("/modify") && c.Body.Contains("UNREAD")) && calls.Any(c => c.Path.EndsWith("/trash")) && calls.All(c => c.Method != "DELETE"),"explicit label and trash actions never permanently delete mail");
        var longSubject = Encoding.UTF8.GetString(GmailMime.Decode(GmailMime.RawMessage("recipient@example.test",new string('я',500),"t")));
        check(longSubject.Split("\r\n").All(line => line.Length < 998),"long Unicode subject is safely folded into MIME encoded words");
        var sendCount = 0;
        using var failing = new GmailClient(_ => Task.FromResult("fake"),new Handler(_ => { sendCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        try { await failing.Send("recipient@example.test","test","body",null,default); } catch (GmailApiException) { }
        check(sendCount == 1,"ambiguous send failure is never automatically retried");
        bool quota = false;
        using var limited = new GmailClient(_ => Task.FromResult("fake"),new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        try { await limited.Labels(default); } catch (GmailApiException ex) { quota = ex.Message == "gmail_quota"; }
        check(quota,"Gmail API errors are classified without returning sensitive response bodies");
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) { token.ThrowIfCancellationRequested(); return respond(request); } }
}
