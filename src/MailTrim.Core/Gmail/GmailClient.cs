using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MailTrim.Core.Gmail;

public sealed class GmailApiException(string code) : Exception(code);

/// <summary>Fixed Google origin, no cookies, no redirects, no retry of mutations.</summary>
public sealed class GmailClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<string>> accessToken;
    public GmailClient(Func<CancellationToken, Task<string>> accessToken, HttpMessageHandler? handler = null)
    {
        this.accessToken = accessToken;
        http = new(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    public async Task<JsonDocument> Request(string path, CancellationToken token, object? body = null)
    {
        if (!path.StartsWith("messages", StringComparison.Ordinal) && !path.StartsWith("labels", StringComparison.Ordinal) && path != "profile") throw new ArgumentException("gmail_path");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(deadline.Token));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new GmailApiException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "gmail_sign_in", HttpStatusCode.Forbidden => "gmail_permissions",
            HttpStatusCode.TooManyRequests => "gmail_quota", HttpStatusCode.NotFound => "gmail_missing", _ => "gmail_request_failed"
        });
        if (response.Content.Headers.ContentLength > 20_000_000) throw new GmailApiException("gmail_size");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream(); byte[] buffer = new byte[16384]; int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
        { if (output.Length + count > 20_000_000) throw new GmailApiException("gmail_size"); output.Write(buffer,0,count); }
        return JsonDocument.Parse(output.ToArray());
    }
    public async Task<string> Address(CancellationToken token)
    { using var data = await Request("profile",token); return GmailMime.String(data.RootElement,"emailAddress"); }
    public async Task<GmailLabel[]> Labels(CancellationToken token)
    {
        using var data = await Request("labels", token);
        return data.RootElement.GetProperty("labels").EnumerateArray().Select(l => new GmailLabel(GmailMime.String(l,"id"), GmailMime.String(l,"name"))).ToArray();
    }
    public async Task<(int Unread, string[] Ids)> InboxStatus(CancellationToken token)
    {
        using var label = await Request("labels/INBOX", token);
        using var messages = await Request("messages?labelIds=INBOX&maxResults=100", token);
        return (label.RootElement.GetProperty("messagesUnread").GetInt32(), Ids(messages.RootElement));
    }
    private static string[] Ids(JsonElement value) => value.TryGetProperty("messages", out var list) ? list.EnumerateArray().Select(m => GmailMime.String(m,"id")).ToArray() : [];
    public async Task<GmailPage> Page(string label, string query, string? pageToken, CancellationToken token)
    {
        var path = "messages?maxResults=30&includeSpamTrash=" + (label is "SPAM" or "TRASH" ? "true" : "false") + (label.Length == 0 ? "" : "&labelIds=" + Escape(label))
            + (query.Length == 0 ? "" : "&q=" + Escape(query)) + (pageToken is null ? "" : "&pageToken=" + Escape(pageToken));
        using var list = await Request(path, token);
        using var slots = new SemaphoreSlim(3);
        var results = await Task.WhenAll(Ids(list.RootElement).Select(async id =>
        {
            await slots.WaitAsync(token);
            try { using var doc = await Request("messages/" + Escape(id) + "?format=metadata", token); return GmailMime.Parse(doc.RootElement); }
            finally { slots.Release(); }
        }));
        var next = GmailMime.String(list.RootElement,"nextPageToken");
        return new(results.OrderByDescending(m => m.ReceivedAt).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray(), next.Length == 0 ? null : next);
    }
    public async Task<GmailMessage> Message(string id, CancellationToken token)
    { using var doc = await Request("messages/" + Escape(id) + "?format=full", token); return GmailMime.Parse(doc.RootElement); }
    public async Task Modify(string id, string[] add, string[] remove, CancellationToken token)
    { using var doc = await Request("messages/" + Escape(id) + "/modify", token, new { addLabelIds = add, removeLabelIds = remove }); }
    public async Task Trash(string id, CancellationToken token)
    { using var doc = await Request("messages/" + Escape(id) + "/trash", token, new { }); }
    public async Task Send(string to, string subject, string text, GmailMessage? reply, CancellationToken token)
    {
        var raw = GmailMime.RawMessage(to, subject, text, reply);
        using var doc = await Request("messages/send", token, reply is null ? new { raw, threadId = (string?)null } : new { raw, threadId = (string?)reply.ThreadId });
    }
    public async Task<byte[]> Attachment(string message, string id, CancellationToken token)
    { using var doc = await Request("messages/" + Escape(message) + "/attachments/" + Escape(id), token); return GmailMime.Decode(GmailMime.String(doc.RootElement,"data")); }
    public void Dispose() => http.Dispose();
}
