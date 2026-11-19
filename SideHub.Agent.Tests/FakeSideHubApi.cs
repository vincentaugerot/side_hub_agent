using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SideHub.Agent.Tests;

/// <summary>
/// A SideHub API on the loopback interface for <c>sidehub-cli</c> tests: answers the longest path suffix registered with
/// <see cref="Respond"/> (the path without its query string, for one method when it names one) and records each
/// request: path and query, method, body.
/// </summary>
internal sealed class FakeSideHubApi : IDisposable
{
    public sealed record Call(string Method, string PathAndQuery, string Body)
    {
        public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Body);
    }

    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<(string? Method, string PathSuffix), (string Body, HttpStatusCode Status, string ContentType)> _responses = new();
    private readonly ConcurrentQueue<Call> _calls = new();

    public string Url { get; }
    public IReadOnlyList<Call> Calls => _calls.ToList();
    public IReadOnlyList<string> Requests => _calls.Select(c => c.PathAndQuery).ToList();
    /// <summary>Each request as <c>METHOD /path</c>, without its query string.</summary>
    public IReadOnlyList<string> Methods => _calls.Select(c => $"{c.Method} {c.PathAndQuery.Split('?')[0]}").ToList();

    public FakeSideHubApi()
    {
        // The free port is released before HttpListener binds it: a test running in parallel may take it first.
        for (var attempt = 1; ; attempt++)
        {
            var listener = new HttpListener();
            var url = $"http://127.0.0.1:{FreePort()}/";
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                (_listener, Url) = (listener, url);
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }
        _ = ServeAsync();
    }

    /// <param name="method">Only for that method (a path read with GET and written with POST); any method when null.</param>
    public void Respond(string pathSuffix, string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json", string? method = null) =>
        _responses[(method, pathSuffix)] = (body, status, contentType);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }

            string requestBody;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                requestBody = await reader.ReadToEndAsync();
            var path = context.Request.Url!.AbsolutePath;
            _calls.Enqueue(new Call(context.Request.HttpMethod, context.Request.Url.PathAndQuery, requestBody));

            var matches = _responses.Keys
                .Where(k => path.EndsWith(k.PathSuffix) && (k.Method is null || k.Method == context.Request.HttpMethod))
                .OrderByDescending(k => k.PathSuffix.Length).ThenByDescending(k => k.Method is not null)
                .ToList();
            var (body, status, contentType) = matches.Count == 0
                ? ("""{"error":"not found"}""", HttpStatusCode.NotFound, "application/json")
                : _responses[matches[0]];
            context.Response.StatusCode = (int)status;
            if (body.Length > 0)
            {
                context.Response.ContentType = contentType;
                var bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose() => _listener.Close();
}
