using System.Text;
using System.Text.Json;

interface IHuddleRecordingSender
{
    Task<HuddleRecordingSendResult> SendAsync(
        string audioFilePath,
        string sessionId,
        CancellationToken cancellationToken = default);
}

sealed class HuddleRecordingSender : IHuddleRecordingSender
{
    private const string FlowUrlEnvironmentVariable = "HUDDLE_TRANSCRIPTION_FLOW_URL";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<HuddleRecordingSendResult> SendAsync(
        string audioFilePath,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A recording session ID is required.", nameof(sessionId));
        }

        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException("The recording file could not be found.", audioFilePath);
        }

        var fileInfo = new FileInfo(audioFilePath);
        if (fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The recording file is empty.");
        }

        var flowUrl = ResolveFlowUrl();
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
        var base64Audio = Convert.ToBase64String(audioBytes);

        var request = new HuddleTranscriptionRequest(
            sessionId,
            Path.GetFileName(audioFilePath),
            "en-US",
            base64Audio);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        using var response = await http.PostAsync(flowUrl, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Power Automate transcription request failed with HTTP {(int)response.StatusCode} {response.StatusCode}."
                + Environment.NewLine
                + responseBody);
        }

        var transcriptionResponse = JsonSerializer.Deserialize<HuddleTranscriptionResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Power Automate returned an empty or invalid JSON response.");

        if (!transcriptionResponse.Success)
        {
            throw new InvalidOperationException(
                "Power Automate transcription reported failure."
                + Environment.NewLine
                + responseBody);
        }

        if (!string.IsNullOrWhiteSpace(transcriptionResponse.SessionId)
            && !string.Equals(transcriptionResponse.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Power Automate returned transcript for session '{transcriptionResponse.SessionId}', but expected '{sessionId}'.");
        }

        var status = string.IsNullOrWhiteSpace(transcriptionResponse.Status)
            ? "Transcription complete."
            : transcriptionResponse.Status;

        return new HuddleRecordingSendResult(
            true,
            "Transcription completed successfully.",
            audioFilePath,
            sessionId,
            status,
            transcriptionResponse.Transcript ?? "");
    }

    private static string ResolveFlowUrl()
    {
        var flowUrl = Environment.GetEnvironmentVariable(FlowUrlEnvironmentVariable)?.Trim();

        if (string.IsNullOrWhiteSpace(flowUrl))
        {
            flowUrl = Environment.GetEnvironmentVariable(FlowUrlEnvironmentVariable, EnvironmentVariableTarget.User)?.Trim();
        }

        if (string.IsNullOrWhiteSpace(flowUrl))
        {
            throw new InvalidOperationException(
                $"Set {FlowUrlEnvironmentVariable} to the Power Automate HTTP trigger URL before sending recordings to Huddle.");
        }

        if (!Uri.TryCreate(flowUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{FlowUrlEnvironmentVariable} must be a valid HTTP or HTTPS URL.");
        }

        return flowUrl;
    }
}

sealed record HuddleTranscriptionRequest(
    string SessionId,
    string FileName,
    string Language,
    string AudioBase64);

sealed record HuddleTranscriptionResponse(
    bool Success,
    string? SessionId,
    string? Status,
    string? Transcript);

sealed record HuddleRecordingSendResult(
    bool Success,
    string Message,
    string AudioFilePath,
    string SessionId,
    string Status,
    string Transcript);
