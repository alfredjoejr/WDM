using System.Net;
using System.Net.Http.Headers;

namespace WDM.Core.Network;

/// <summary>
/// Provides high-performance, socket-optimized HttpClient instances with HTTP/3 support.
/// </summary>
public static class HttpClientFactory
{
    private static readonly ProductInfoHeaderValue DefaultUserAgent = 
        new("WDM", "1.0");

    public static HttpClient CreateClient(bool preferHttp3 = true, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(10),
            DefaultRequestVersion = preferHttp3 ? HttpVersion.Version30 : HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        client.DefaultRequestHeaders.UserAgent.Add(DefaultUserAgent);
        client.DefaultRequestHeaders.Add("Accept", "*/*");

        return client;
    }
}
