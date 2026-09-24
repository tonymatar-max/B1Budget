using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace B1Budget.Api.Services.B1;

public class B1ConnectionInfo
{
    public string BaseUrl { get; set; } = "";
    public string CompanyDB { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IgnoreSslErrors { get; set; } = true;
}

public class ServiceLayerException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Minimal Service Layer client: login with transparent session recovery, OData GET with
/// paging, POST/PATCH, and SQLQueries (create + execute with parameters).
/// </summary>
public class ServiceLayerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly B1ConnectionInfo _conn;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public ServiceLayerClient(B1ConnectionInfo conn)
    {
        _conn = conn;
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        if (conn.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(conn.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    // ---------------------------------------------------------------- session

    public async Task LoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct);
        try
        {
            var body = JsonSerializer.Serialize(new { _conn.CompanyDB, _conn.UserName, _conn.Password });
            using var resp = await _http.PostAsync("Login", new StringContent(body, Encoding.UTF8, "application/json"), ct);
            var content = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new ServiceLayerException($"Login failed: {ExtractError(content)}", (int)resp.StatusCode);
            _loggedIn = true;
        }
        finally { _loginLock.Release(); }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (!_loggedIn) await LoginAsync(ct);
        var resp = await _http.SendAsync(build(), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            await LoginAsync(ct);   // session timed out (B1 default 30 min)
            resp = await _http.SendAsync(build(), ct);
        }
        return resp;
    }

    private async Task<string> SendForStringAsync(Func<HttpRequestMessage> build, string what, CancellationToken ct)
    {
        using var resp = await SendAsync(build, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ServiceLayerException($"{what} failed ({(int)resp.StatusCode}): {ExtractError(content)}", (int)resp.StatusCode);
        return content;
    }

    // ---------------------------------------------------------------- reads

    public Task<string> GetMetadataAsync(CancellationToken ct = default) =>
        SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "$metadata"), "$metadata", ct);

    /// <summary>GET an OData collection, following nextLink until exhausted.</summary>
    public async Task<List<JsonObject>> QueryAllAsync(string relativeUrl, CancellationToken ct = default)
    {
        var all = new List<JsonObject>();
        string? url = relativeUrl;
        while (url != null)
        {
            var current = url;
            var content = await SendForStringAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=1000");
                return req;
            }, $"GET {current}", ct);
            var node = JsonNode.Parse(content) as JsonObject;
            if (node?["value"] is JsonArray arr)
                all.AddRange(arr.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
            url = NextLink(node);
        }
        return all;
    }

    public async Task<JsonObject> GetAsync(string relativeUrl, CancellationToken ct = default)
    {
        var content = await SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, relativeUrl), $"GET {relativeUrl}", ct);
        return JsonNode.Parse(content) as JsonObject ?? new JsonObject();
    }

    // ---------------------------------------------------------------- writes

    public async Task<JsonObject> PostAsync(string entitySet, JsonNode body, CancellationToken ct = default)
    {
        var json = body.ToJsonString();
        var content = await SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Post, entitySet)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }, $"POST {entitySet}", ct);
        return string.IsNullOrWhiteSpace(content) ? new JsonObject() : JsonNode.Parse(content) as JsonObject ?? new JsonObject();
    }

    public async Task PatchAsync(string entityPath, JsonNode body, CancellationToken ct = default)
    {
        var json = body.ToJsonString();
        await SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Patch, entityPath)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }, $"PATCH {entityPath}", ct);
    }

    // ---------------------------------------------------------------- SQLQueries

    /// <summary>
    /// Make sure a stored SQL query exists with exactly this text (created or replaced), so the
    /// app owns its queries and a changed dimension column takes effect immediately.
    /// </summary>
    public async Task EnsureSqlQueryAsync(string code, string name, string sql, CancellationToken ct = default)
    {
        using (var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"SQLQueries('{code}')"), ct))
        {
            if (resp.IsSuccessStatusCode)
            {
                var existing = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                // SL stores its own normalised copy (identifiers wrapped in [..]) — compare ignoring that.
                if (NormalizeSql(existing?["SqlText"]?.GetValue<string>()) == NormalizeSql(sql)) return;
                await PatchAsync($"SQLQueries('{code}')", new JsonObject { ["SqlText"] = sql, ["SqlName"] = name }, ct);
                return;
            }
        }
        await PostAsync("SQLQueries", new JsonObject { ["SqlCode"] = code, ["SqlName"] = name, ["SqlText"] = sql }, ct);
    }

    /// <summary>Run a stored query with :named parameters (values are passed already SQL-quoted).</summary>
    public Task<List<JsonObject>> RunSqlQueryAsync(string code, IDictionary<string, string> parameters, CancellationToken ct = default)
    {
        var qs = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return QueryAllAsync($"SQLQueries('{code}')/List" + (qs.Length > 0 ? "?" + qs : ""), ct);
    }

    // ---------------------------------------------------------------- helpers

    private static string NormalizeSql(string? sql) =>
        string.Concat((sql ?? "").Where(c => c is not ('[' or ']' or '"') && !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static string? NextLink(JsonObject? node)
    {
        var link = node?["odata.nextLink"]?.GetValue<string>() ?? node?["@odata.nextLink"]?.GetValue<string>();
        if (string.IsNullOrEmpty(link)) return null;
        // SL returns nextLink relative to /b1s/v1/ (e.g. "Budgets?$skip=20") — strip any leading path.
        var idx = link.IndexOf("/b1s/v", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var after = link.IndexOf('/', idx + 6);
            link = after >= 0 ? link[(after + 1)..] : link;
        }
        return link.TrimStart('/');
    }

    public static string ExtractError(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty response)";
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
            {
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("value", out var v)) return v.GetString() ?? content;
                if (msg.ValueKind == JsonValueKind.String) return msg.GetString() ?? content;
            }
        }
        catch { /* not JSON */ }
        var t = content.Trim();
        return t.Length > 400 ? t[..400] : t;
    }

    public void Dispose() { _http.Dispose(); _loginLock.Dispose(); }
}
