using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace MailTrim.App;

/// <summary>Remote images are optional, cookie-free and bounded. At most two requests per reader.</summary>
public sealed class ReaderImageLoader : IDisposable
{
    private readonly HttpClient client;
    private readonly SemaphoreSlim slots = new(2);
    public ReaderImageLoader(HttpMessageHandler? handler = null) => client = new(handler ?? new HttpClientHandler
        { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public async Task<BitmapSource> Load(Uri uri, CancellationToken token)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new IOException("image_origin");
        await slots.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var requestToken = deadline.Token;
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, requestToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 8_000_000) throw new IOException("image_size");
            await using var input = await response.Content.ReadAsStreamAsync(requestToken);
            using var data = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, requestToken)) > 0)
            { if (data.Length + count > 8_000_000) throw new IOException("image_size"); data.Write(buffer, 0, count); }
            requestToken.ThrowIfCancellationRequested(); data.Position = 0;
            var frame = BitmapDecoder.Create(data, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var width = frame.PixelWidth; var height = frame.PixelHeight;
            if (width <= 0 || height <= 0 || width > 20000 || height > 20000 || (long)width * height > 40_000_000) throw new IOException("image_pixels");
            var scale = Math.Min(1, Math.Min(1400.0 / width, 4000.0 / height));
            data.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale)); bitmap.DecodePixelHeight = Math.Max(1, (int)(height * scale));
            bitmap.StreamSource = data; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        finally { slots.Release(); }
    }
    public void Dispose() => client.Dispose();
}
