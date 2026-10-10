using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MailTrim.Core.Gmail;

public sealed record GmailLabel(string Id, string Name, int? Unread = null)
{
    public override string ToString() => Name;
}
public sealed record GmailAttachment(string Id, string Name, string MimeType, int Size);
public sealed record GmailMessage(string Id, string ThreadId, string From, string To, string Subject,
    string Preview, DateTimeOffset ReceivedAt, string[] Labels, string Text, string[] Images,
    GmailAttachment[] Attachments, string ReplyTo, string MessageId)
{
    public bool Unread => Labels.Contains("UNREAD");
    public override string ToString() => $"{(Unread ? "● " : "")}{From}\n{Subject}\n{ReceivedAt.LocalDateTime:g}";
}
public sealed record GmailPage(GmailMessage[] Messages, string? NextPageToken);

public static class GmailMime
{
    static GmailMime() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string data)
    {
        if (data.Length > 28_000_000) throw new FormatException("mime_size");
        var padded = data.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
    public static string PkceChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string String(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    public static string[] Strings(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
    public static GmailMessage Parse(JsonElement value)
    {
        var payload = value.GetProperty("payload");
        var headers = payload.TryGetProperty("headers", out var hs) ? hs.EnumerateArray().ToArray() : [];
        string Header(string name) => headers.FirstOrDefault(h => String(h, "name").Equals(name, StringComparison.OrdinalIgnoreCase)) is var h && h.ValueKind == JsonValueKind.Object ? String(h, "value") : "";
        var plain = new List<string>(); var html = new List<string>(); var attachments = new List<GmailAttachment>();
        void Visit(JsonElement part, int depth)
        {
            if (depth > 30) return;
            var type = String(part, "mimeType"); var file = String(part, "filename");
            if (part.TryGetProperty("body", out var body))
            {
                var attachmentId = String(body, "attachmentId");
                if (attachmentId.Length > 0) attachments.Add(new(attachmentId, file.Length > 0 ? file : "Вложение", type, body.TryGetProperty("size", out var size) ? size.GetInt32() : 0));
                else if (file.Length == 0 && String(body, "data") is { Length: > 0 } data)
                {
                    // Gmail generally returns UTF-8; honor common MIME charsets for older mail.
                    var charset = part.TryGetProperty("headers", out var ph) ? ph.EnumerateArray().FirstOrDefault(h => String(h,"name").Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) : default;
                    var match = Regex.Match(charset.ValueKind == JsonValueKind.Object ? String(charset,"value") : "", "charset=[\"']?([\\w-]+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                    Encoding encoding = Encoding.UTF8;
                    if (match.Success) { try { encoding = Encoding.GetEncoding(match.Groups[1].Value); } catch (ArgumentException) { } }
                    var text = encoding.GetString(Decode(data));
                    if (type == "text/plain") plain.Add(text);
                    else if (type == "text/html") html.Add(text);
                }
            }
            if (part.TryGetProperty("parts", out var parts)) foreach (var child in parts.EnumerateArray()) Visit(child, depth + 1);
        }
        Visit(payload, 0);
        var markup = string.Join("\n", html); var images = new List<string>();
        foreach (Match match in Regex.Matches(markup, "<img\\b[^>]*?\\bsrc\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300)))
            if (match.Groups[1].Value.Length <= 2048 && Uri.TryCreate(WebUtility.HtmlDecode(match.Groups[1].Value), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && images.Count < 50) images.Add(uri.AbsoluteUri);
        // HTML is reduced to inert native text; it is never executed or injected into a browser.
        string textOnly = plain.Count > 0 ? string.Join("\n\n", plain) : HtmlText(markup);
        var millis = long.TryParse(String(value,"internalDate"), out var ms) ? ms : 0;
        return new(String(value,"id"), String(value,"threadId"), Header("From"), Header("To"), Header("Subject"),
            WebUtility.HtmlDecode(String(value,"snippet")), DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(millis, 0, 253402300799999)),
            Strings(value,"labelIds"), textOnly.Length > 1_000_000 ? textOnly[..1_000_000] : textOnly,
            images.Distinct().ToArray(), attachments.ToArray(), Header("Reply-To"), Header("Message-ID"));
    }
    public static string HtmlText(string html)
    {
        var timeout = TimeSpan.FromMilliseconds(300);
        var text = Regex.Replace(html, "<(script|style)\\b[^>]*>[\\s\\S]*?</\\1\\s*>", "", RegexOptions.IgnoreCase, timeout);
        text = Regex.Replace(text, "<\\s*(br|/p|/div|/tr|/li|/h[1-6])\\b[^>]*>", "\n", RegexOptions.IgnoreCase, timeout);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", "", RegexOptions.None, timeout));
        return Regex.Replace(text, "[\\t ]+", " ", RegexOptions.None, timeout).Trim();
    }
    public static string RawMessage(string to, string subject, string text, GmailMessage? reply = null)
    {
        if (to.Contains('\r') || to.Contains('\n') || subject.Contains('\r') || subject.Contains('\n')) throw new FormatException("header_injection");
        var recipients = new MailAddressCollection(); recipients.Add(to);
        if (recipients.Count is < 1 or > 50 || text.Length > 1_000_000 || subject.Length > 500) throw new FormatException("message_size");
        if (recipients.Any(r => r.Address.Length > 254)) throw new FormatException("address_size");
        var words = new List<string>(); var chunk = new StringBuilder();
        foreach (var rune in subject.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(chunk.ToString()) + rune.Utf8SequenceLength > 36)
            { words.Add("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) + "?="); chunk.Clear(); }
            chunk.Append(rune.ToString());
        }
        if (chunk.Length > 0) words.Add("=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) + "?=");
        var headers = $"To: {string.Join(",\r\n ", recipients.Select(r => r.Address))}\r\nSubject: {string.Join("\r\n ",words)}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n";
        if (reply?.MessageId is { Length: > 0 and < 500 } id && Regex.IsMatch(id, "^<[^<>\\r\\n ]+>$", RegexOptions.None, TimeSpan.FromMilliseconds(100))) headers += $"In-Reply-To: {id}\r\nReferences: {id}\r\n";
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(text), Base64FormattingOptions.InsertLineBreaks);
        return Base64Url(Encoding.UTF8.GetBytes(headers + "\r\n" + body));
    }
}
