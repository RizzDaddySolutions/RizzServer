using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RizzServer.Core.Api;

/// <summary>An error answered by the service (or a failure to reach it, with <see cref="Status"/> = 0).</summary>
public sealed class ServiceApiException(HttpStatusCode status, string message, IReadOnlyList<string>? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode Status { get; } = status;
    public IReadOnlyList<string> Details { get; } = details ?? [];

    public bool IsUnreachable => Status == 0;
    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;
    public bool IsConflict => Status == HttpStatusCode.Conflict;

    /// <summary>The message plus any per-problem details, ready to show to a user.</summary>
    public string FullMessage => Details.Count == 0 ? Message : Message + Environment.NewLine + string.Join(Environment.NewLine, Details.Select(d => "• " + d));
}

/// <summary>Typed client for the headless service's REST API (<c>/api/v1</c>).</summary>
public sealed class ServiceClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly HttpClient _stream;   // no timeout: lives as long as the stream does

    public ServiceClient(Uri baseUri, string apiKey, HttpMessageHandler? handler = null)
    {
        BaseUri = baseUri;
        _http = Create(baseUri, apiKey, handler, TimeSpan.FromSeconds(15));
        _stream = Create(baseUri, apiKey, handler, Timeout.InfiniteTimeSpan);
    }

    public Uri BaseUri { get; }

    private static HttpClient Create(Uri baseUri, string apiKey, HttpMessageHandler? handler, TimeSpan timeout)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = baseUri;
        client.Timeout = timeout;
        if (apiKey.Length > 0) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    public void Dispose()
    {
        _http.Dispose();
        _stream.Dispose();
    }

    // ---- runtime ----
    public Task<StatusBody> GetStatusAsync(CancellationToken ct = default) => GetAsync<StatusBody>("api/v1/runtime", ct);
    public Task<StatusBody> StartAsync(CancellationToken ct = default) => PostAsync<StatusBody>("api/v1/runtime/start", ct);
    public Task<StatusBody> StopAsync(CancellationToken ct = default) => PostAsync<StatusBody>("api/v1/runtime/stop", ct);
    public Task<StatusBody> RestartAsync(CancellationToken ct = default) => PostAsync<StatusBody>("api/v1/runtime/restart", ct);
    public Task<List<DeviceStatusBody>> GetDeviceStatusAsync(CancellationToken ct = default) => GetAsync<List<DeviceStatusBody>>("api/v1/devices/status", ct);

    // ---- configuration ----
    public Task<ProjectBody> GetProjectAsync(CancellationToken ct = default) => GetAsync<ProjectBody>("api/v1/config", ct);

    public async Task PutProjectAsync(ProjectBody project, CancellationToken ct = default) =>
        await Send<ProjectBody>(HttpMethod.Put, "api/v1/config", JsonContent.Create(project, options: Json), ct);

    /// <returns>The user names after the change.</returns>
    public Task<List<string>> AddUserAsync(string username, string password, CancellationToken ct = default) =>
        Send<List<string>>(HttpMethod.Post, "api/v1/server/users", JsonContent.Create(new UserBody(username, password), options: Json), ct);

    public Task<List<string>> RemoveUserAsync(string username, CancellationToken ct = default) =>
        Send<List<string>>(HttpMethod.Delete, $"api/v1/server/users/{Uri.EscapeDataString(username)}", null, ct);

    // ---- live data ----
    public Task<List<TagBody>> GetTagsAsync(CancellationToken ct = default) => GetAsync<List<TagBody>>("api/v1/tags", ct);

    public async Task WriteTagAsync(string path, object value, CancellationToken ct = default)
    {
        var body = new WriteBody(JsonSerializer.SerializeToElement(value, Json));
        await Send<TagBody>(HttpMethod.Put, "api/v1/tags/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)),
            JsonContent.Create(body, options: Json), ct);
    }

    public Task<List<LogLine>> GetLogAsync(int limit = 200, CancellationToken ct = default) => GetAsync<List<LogLine>>($"api/v1/log?limit={limit}", ct);

    /// <summary>Value changes as they happen (Server-Sent Events). Ends when the stream breaks; callers reconnect.</summary>
    public async IAsyncEnumerable<TagBody> StreamTagsAsync(bool snapshot, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/stream" + (snapshot ? "?snapshot=true" : ""));
        using var response = await SendRaw(_stream, request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(line.AsSpan(5).TrimStart());
            else if (line.Length == 0 && data.Length > 0)
            {
                TagBody? tag = null;
                try { tag = JsonSerializer.Deserialize<TagBody>(data.ToString(), Json); }
                catch (JsonException) { /* ignore a malformed event */ }
                data.Clear();
                if (tag is not null) yield return tag;
            }
        }
    }

    // ---- plumbing ----
    private Task<T> GetAsync<T>(string url, CancellationToken ct) => Send<T>(HttpMethod.Get, url, null, ct);
    private Task<T> PostAsync<T>(string url, CancellationToken ct) => Send<T>(HttpMethod.Post, url, null, ct);

    private async Task<T> Send<T>(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        using var response = await SendRaw(_http, request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccess(response);
        try
        {
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ServiceApiException(response.StatusCode, "The service answered with something that is not RizzServer's API.", inner: ex);
        }
    }

    private static async Task<HttpResponseMessage> SendRaw(HttpClient client, HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        try { return await client.SendAsync(request, option, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var message = ex is TaskCanceledException ? "The service did not answer in time." : "The service could not be reached: " + (ex.InnerException?.Message ?? ex.Message);
            throw new ServiceApiException(0, message, inner: ex);
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The service rejected the API key.",
            HttpStatusCode.NotFound => "The service has no such endpoint; is this a RizzServer service of the same version?",
            _ => $"The service answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
        IReadOnlyList<string>? details = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(Json);
            if (body is { Error.Length: > 0 }) { message = body.Error; details = body.Details; }
        }
        catch (Exception) { /* not our error format: keep the generic message */ }
        throw new ServiceApiException(response.StatusCode, message, details);
    }
}
