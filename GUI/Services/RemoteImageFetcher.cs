using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Kscan.Core.Imaging;
using Kscan.Core.Net;

namespace Kscan.App.Services;

/// <summary>
/// 把剪贴板里的<b>图片地址</b>下载成像素帧（贴图用）。
/// <para>
/// 这是整个程序里<b>唯一</b>会联网的地方，所以规矩都在这里：
/// 只认 http/https；内网 / 环回 / 链路本地地址一律拒绝（防 SSRF）；
/// 只接受图片响应；限制大小与超时；同一地址落盘缓存，重复贴不重复下载。
/// </para>
/// <para>
/// 调用方必须先用设置里的开关把关（默认关闭），这里不做"要不要联网"的判断。
/// </para>
/// </summary>
internal static class RemoteImageFetcher
{
    /// <summary>整体超时。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>响应大小上限：超过就放弃（防止被塞一个几百 MB 的文件）。</summary>
    private const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>跟随重定向的上限。</summary>
    private const int MaxRedirects = 3;

    /// <summary>认作图片的扩展名。</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// 这段文本像不像"一个图片地址"：单个 http(s) 地址，且路径扩展名是图片格式。
    /// 判定规则在 <see cref="ImageUrlPolicy"/>（纯逻辑、可单测）。
    /// </summary>
    public static bool LooksLikeImageUrl(string? text, out Uri? uri) =>
        ImageUrlPolicy.LooksLikeImageUrl(text, out uri);

    /// <summary>
    /// 下载并解码。成功返回帧；任何一步失败（含安全拦截）返回 <c>null</c>，由调用方回退到贴文本。
    /// </summary>
    public static async Task<ImageFrame?> TryFetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(uri))
        {
            GuiLog.Write($"remote-image: blocked {uri.Host}");
            return null;
        }

        try
        {
            var cached = await TryReadCacheAsync(uri, cancellationToken);
            if (cached is not null)
            {
                GuiLog.Write($"remote-image: cache hit {uri.Host}");
                return cached;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                GuiLog.Write($"remote-image: http {(int)response.StatusCode} {uri.Host}");
                return null;
            }

            if (!IsImageResponse(response, uri))
            {
                GuiLog.Write($"remote-image: not an image ({response.Content.Headers.ContentType}) {uri.Host}");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                GuiLog.Write($"remote-image: too large ({response.Content.Headers.ContentLength} bytes) {uri.Host}");
                return null;
            }

            var bytes = await ReadLimitedAsync(response, timeout.Token);
            if (bytes is null)
            {
                return null;
            }

            var frame = Decode(bytes);
            if (frame is null)
            {
                GuiLog.Write($"remote-image: decode failed {uri.Host}");
                return null;
            }

            await TryWriteCacheAsync(uri, bytes, cancellationToken);
            GuiLog.Write($"remote-image: fetched {frame.Width}x{frame.Height} {uri.Host}");
            return frame;
        }
        catch (OperationCanceledException)
        {
            GuiLog.Write($"remote-image: timeout {uri.Host}");
            return null;
        }
        catch (Exception ex)
        {
            GuiLog.Write($"remote-image: failed ({ex.GetType().Name}: {ex.Message}) {uri.Host}");
            return null;
        }
    }

    /// <summary>只放行公网 http/https（规则见 <see cref="ImageUrlPolicy.IsPublicHttpUrl"/>）。</summary>
    internal static bool IsAllowed(Uri uri) => ImageUrlPolicy.IsPublicHttpUrl(uri);

    private static bool IsImageResponse(HttpResponseMessage response, Uri uri) =>
        ImageUrlPolicy.IsImageContentType(response.Content.Headers.ContentType?.MediaType, uri);

    private static async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                GuiLog.Write("remote-image: too large (stream)");
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ImageFrame? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return SkiaImageCodec.FromEncoded(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout,
            // 有些图床对默认 UA 直接 403
            DefaultRequestHeaders = { { "User-Agent", "kscan/0.1.10" } },
        };

        return client;
    }

    /// <summary>缓存目录：<c>%APPDATA%\kscan\cache\remote</c>。</summary>
    private static string CacheDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.GetTempPath();
            }

            return Path.Combine(root, "kscan", "cache", "remote");
        }
    }

    private static string CachePath(Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16];
        return Path.Combine(CacheDirectory, hash.ToLowerInvariant() + ".img");
    }

    private static async Task<ImageFrame?> TryReadCacheAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var path = CachePath(uri);
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Decode(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task TryWriteCacheAsync(Uri uri, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(CachePath(uri), bytes, cancellationToken);
        }
        catch (Exception)
        {
            // 缓存失败不影响这次贴图
        }
    }
}
