using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

sealed class LocalBridgeServer : IDisposable
{
    private const string TokenHeader = "X-Huddle-Bridge-Token";
    private readonly LocalRecordingService recordingService;
    private readonly TcpListener listener = new(IPAddress.Loopback, AppInfo.BridgePort);
    private readonly CancellationTokenSource stopRequested = new();
    private readonly HashSet<string> allowedOrigins;
    private bool disposed;

    public LocalBridgeServer(LocalRecordingService recordingService, string bridgeToken)
    {
        this.recordingService = recordingService;
        BridgeToken = bridgeToken;
        allowedOrigins = LoadAllowedOrigins();
    }

    public string BridgeToken { get; }

    public string Url => AppInfo.BridgeUrl;

    public string AllowedOriginsDisplay => allowedOrigins.Count == 0
        ? "(none configured; set HUDDLE_BRIDGE_ALLOWED_ORIGINS)"
        : string.Join(", ", allowedOrigins);

    public void Start()
    {
        listener.Start();
        _ = Task.Run(() => RunAsync(stopRequested.Token));
        BridgeLogger.Log($"Local bridge listening url={Url} allowedOrigins=\"{AllowedOriginsDisplay}\"");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        stopRequested.Cancel();
        listener.Stop();
        stopRequested.Dispose();
        disposed = true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                BridgeLogger.Log($"Bridge listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;

        try
        {
            using var stream = client.GetStream();
            var request = await HttpRequestData.ReadAsync(stream);

            if (request is null)
            {
                return;
            }

            if (request.Headers.TryGetValue("Origin", out var origin) && !string.IsNullOrWhiteSpace(origin))
            {
                BridgeLogger.Log($"Request origin={origin}");
            }

            BridgeLogger.Log($"{request.Method} {request.Path}");

            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, HttpStatusCode.NoContent, "", "application/json", origin);
                return;
            }

            if (request.Method == "GET" && request.Path.TrimEnd('/') == "/health")
            {
                BridgeLogger.Log("Health request");
                await WriteJsonAsync(stream, HttpStatusCode.OK, new { status = recordingService.Status, version = AppInfo.Version }, origin);
                return;
            }

            if (!IsAuthorized(request))
            {
                await WriteJsonAsync(stream, HttpStatusCode.Unauthorized, new { success = false, error = "Missing or invalid bridge token." }, origin);
                return;
            }

            await DispatchAsync(stream, request, origin);
        }
        catch (RecordingConflictException ex)
        {
            BridgeLogger.Log($"API conflict: {ex.Message}");
            await WriteJsonSafeAsync(client, HttpStatusCode.Conflict, new { success = false, error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            BridgeLogger.Log($"API validation error: {ex.Message}");
            await WriteJsonSafeAsync(client, HttpStatusCode.BadRequest, new { success = false, error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            BridgeLogger.Log($"API not found: {ex.Message}");
            await WriteJsonSafeAsync(client, HttpStatusCode.NotFound, new { success = false, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            BridgeLogger.Log($"API invalid operation: {ex.Message}");
            await WriteJsonSafeAsync(client, HttpStatusCode.Conflict, new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            BridgeLogger.Log($"API error: {ex.Message}");
            await WriteJsonSafeAsync(client, HttpStatusCode.InternalServerError, new { success = false, error = ex.Message });
        }
    }

    private async Task DispatchAsync(Stream stream, HttpRequestData request, string? origin)
    {
        var path = request.Path.TrimEnd('/');

        if (request.Method == "POST" && path == "/recording/start")
        {
            var sessionRequest = request.ReadJson<SessionRequest>();
            var result = recordingService.Start(sessionRequest.SessionId);
            await WriteJsonAsync(stream, HttpStatusCode.OK, new { success = result.Success, sessionId = result.SessionId, status = result.Status }, origin);
            return;
        }

        if (request.Method == "POST" && path == "/recording/stop")
        {
            var sessionRequest = request.ReadJson<SessionRequest>();
            var result = await recordingService.StopAsync(sessionRequest.SessionId);
            await WriteJsonAsync(stream, HttpStatusCode.OK, new
            {
                success = result.Success,
                sessionId = result.SessionId,
                status = result.Status,
                durationMilliseconds = result.DurationMilliseconds,
                hasAudibleAudio = result.HasAudibleAudio
            }, origin);
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 && segments[0] == "recording" && request.Method == "DELETE")
        {
            recordingService.Delete(Uri.UnescapeDataString(segments[1]));
            await WriteJsonAsync(stream, HttpStatusCode.OK, new { success = true }, origin);
            return;
        }

        if (segments.Length == 3 && segments[0] == "recording")
        {
            var sessionId = Uri.UnescapeDataString(segments[1]);

            if (request.Method == "GET" && segments[2] == "status")
            {
                var result = recordingService.GetStatus(sessionId);
                await WriteJsonAsync(stream, HttpStatusCode.OK, new
                {
                    sessionId = result.SessionId,
                    status = result.Status,
                    durationMilliseconds = result.DurationMilliseconds,
                    peak = result.Peak,
                    audioReady = result.AudioReady,
                    hasAudibleAudio = result.HasAudibleAudio
                }, origin);
                return;
            }

            if (request.Method == "GET" && segments[2] == "audio")
            {
                var session = recordingService.GetCompletedSession(sessionId);
                var fileInfo = new FileInfo(session.AudioFilePath);
                if (!fileInfo.Exists || fileInfo.Length == 0)
                {
                    await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { success = false, error = "Audio file was not found." }, origin);
                    return;
                }

                await WriteFileAsync(stream, fileInfo, origin);
                BridgeLogger.Log($"Audio retrieval sessionId={sessionId} bytes={fileInfo.Length}");
                return;
            }
        }

        await WriteJsonAsync(stream, HttpStatusCode.NotFound, new { success = false, error = "Endpoint not found." }, origin);
    }

    private bool IsAuthorized(HttpRequestData request)
    {
        return request.Headers.TryGetValue(TokenHeader, out var token)
            && string.Equals(token, BridgeToken, StringComparison.Ordinal);
    }

    private async Task WriteJsonSafeAsync(TcpClient client, HttpStatusCode statusCode, object value)
    {
        try
        {
            if (client.Connected)
            {
                using var stream = client.GetStream();
                await WriteJsonAsync(stream, statusCode, value, null);
            }
        }
        catch
        {
            // Nothing useful left to do if writing the error response fails.
        }
    }

    private async Task WriteJsonAsync(Stream stream, HttpStatusCode statusCode, object value, string? origin)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await WriteResponseAsync(stream, statusCode, json, "application/json", origin);
    }

    private async Task WriteFileAsync(Stream stream, FileInfo fileInfo, string? origin)
    {
        var headers = BuildHeaders(HttpStatusCode.OK, "audio/wav", fileInfo.Length, origin);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(headers));
        await using var fileStream = fileInfo.OpenRead();
        await fileStream.CopyToAsync(stream);
    }

    private async Task WriteResponseAsync(Stream stream, HttpStatusCode statusCode, string body, string contentType, string? origin)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = BuildHeaders(statusCode, contentType, bodyBytes.Length, origin);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(headers));
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes);
        }
    }

    private string BuildHeaders(HttpStatusCode statusCode, string contentType, long contentLength, string? origin)
    {
        var builder = new StringBuilder();
        builder.Append($"HTTP/1.1 {(int)statusCode} {statusCode}\r\n");
        builder.Append($"Content-Type: {contentType}\r\n");
        builder.Append($"Content-Length: {contentLength}\r\n");
        builder.Append("Connection: close\r\n");

        if (!string.IsNullOrWhiteSpace(origin) && allowedOrigins.Contains(origin))
        {
            builder.Append($"Access-Control-Allow-Origin: {origin}\r\n");
            builder.Append("Vary: Origin\r\n");
            builder.Append($"Access-Control-Allow-Headers: Content-Type, {TokenHeader}\r\n");
            builder.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    private static HashSet<string> LoadAllowedOrigins()
    {
        var raw = Environment.GetEnvironmentVariable("HUDDLE_BRIDGE_ALLOWED_ORIGINS");
        return string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SessionRequest(string SessionId);

    private sealed class HttpRequestData
    {
        private HttpRequestData(string method, string path, Dictionary<string, string> headers, string body)
        {
            Method = method;
            Path = path;
            Headers = headers;
            Body = body;
        }

        public string Method { get; }

        public string Path { get; }

        public Dictionary<string, string> Headers { get; }

        private string Body { get; }

        public static async Task<HttpRequestData?> ReadAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return null;
            }

            var requestParts = requestLine.Split(' ', 3);
            if (requestParts.Length < 2)
            {
                throw new ArgumentException("Invalid HTTP request line.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            var contentLength = headers.TryGetValue("Content-Length", out var rawLength)
                && int.TryParse(rawLength, out var parsedLength)
                ? parsedLength
                : 0;

            var body = "";
            if (contentLength > 0)
            {
                var buffer = new char[contentLength];
                var read = await reader.ReadBlockAsync(buffer, 0, contentLength);
                body = new string(buffer, 0, read);
            }

            return new HttpRequestData(requestParts[0], requestParts[1], headers, body);
        }

        public T ReadJson<T>()
        {
            var value = JsonSerializer.Deserialize<T>(Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value ?? throw new ArgumentException("Request body is required.");
        }
    }
}
