using System.Net;
using System.Text;
using System.Text.Json;

namespace MailTrim.Core;

public enum UpdateCheckError { Network, Timeout, RateLimit, AccessDenied, NotFound, Server, InvalidResponse, ResponseTooLarge }
public sealed class UpdateCheckException(UpdateCheckError error) : Exception(error.ToString())
{
    public UpdateCheckError Error { get; } = error;
}
public static class UpdateCatalogClient
{
    public static async Task<ReleaseCandidate?> Fetch(HttpClient client, Uri endpoint, bool preview, CancellationToken token = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateCheckException(UpdateCheckError.NotFound);
                if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))
                    throw new UpdateCheckException(UpdateCheckError.RateLimit);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new UpdateCheckException(UpdateCheckError.AccessDenied);
                if ((int)response.StatusCode >= 500) throw new UpdateCheckException(UpdateCheckError.Server);
                if (!response.IsSuccessStatusCode) throw new UpdateCheckException(UpdateCheckError.InvalidResponse);
                const int limit = 4_000_000;
                if (response.Content.Headers.ContentLength > limit) throw new UpdateCheckException(UpdateCheckError.ResponseTooLarge);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var data = new MemoryStream(); var buffer = new byte[16384]; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (data.Length + read > limit) throw new UpdateCheckException(UpdateCheckError.ResponseTooLarge);
                    data.Write(buffer, 0, read);
                }
                return ReleaseCatalog.Select(Encoding.UTF8.GetString(data.ToArray()), preview);
            }
            catch (JsonException) { throw new UpdateCheckException(UpdateCheckError.InvalidResponse); }
            catch (UpdateCheckException ex) when (ex.Error == UpdateCheckError.Server && attempt == 0) { }
            catch (HttpRequestException) { if (attempt == 1) throw new UpdateCheckException(UpdateCheckError.Network); }
            catch (IOException) { if (attempt == 1) throw new UpdateCheckException(UpdateCheckError.Network); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { if (attempt == 1) throw new UpdateCheckException(UpdateCheckError.Timeout); }
            await Task.Delay(250, token);
        }
        throw new UpdateCheckException(UpdateCheckError.Network);
    }
}
