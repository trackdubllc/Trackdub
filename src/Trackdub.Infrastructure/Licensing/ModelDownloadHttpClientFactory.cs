using System.Net.Http.Headers;

namespace Trackdub.Infrastructure.Licensing;

/// <summary>
/// Long-lived HTTP client for model downloads. Registered as a singleton in composition.
/// </summary>
public sealed class ModelDownloadHttpClient
{
    public ModelDownloadHttpClient(HuggingFaceDownloadOptions options)
    {
        Client = ModelDownloadHttpClientFactory.Create(options);
    }

    public HttpClient Client { get; }
}

/// <summary>
/// Builds a long-lived <see cref="HttpClient"/> tuned for parallel Hugging Face model downloads.
/// </summary>
public static class ModelDownloadHttpClientFactory
{
    public const string HttpClientName = "trackdub-model-download";

    public static HttpClient Create(HuggingFaceDownloadOptions? options = null)
    {
        options ??= HuggingFaceDownloadOptions.FromEnvironment();
        int maxConnections = Math.Max(options.MaxParallelConnections + 4, 12);

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            MaxConnectionsPerServer = maxConnections,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(60),
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromHours(6),
        };

        ApplyAuthentication(client);
        return client;
    }

    internal static void ApplyAuthentication(HttpRequestMessage request)
    {
        string? token = ResolveHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static void ApplyAuthentication(HttpClient client)
    {
        string? token = ResolveHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static string? ResolveHubToken() =>
        Environment.GetEnvironmentVariable("HF_TOKEN")
        ?? Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN");
}
