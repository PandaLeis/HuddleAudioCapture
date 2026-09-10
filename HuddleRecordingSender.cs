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
    private const string DefaultLanguage = "en-US";
    private const string Source = "ComputerAudio";
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
        BridgeLogger.Log($"Transcription submission started sessionId={sessionId} fileName=\"{fileInfo.Name}\" bytes={fileInfo.Length}");

        var audioBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
        var base64Audio = Convert.ToBase64String(audioBytes);

        var request = new HuddleTranscriptionRequest(
            sessionId,
            Path.GetFileName(audioFilePath),
            DefaultLanguage,
            Source,
            base64Audio);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        using var response = await http.PostAsync(flowUrl, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        BridgeLogger.Log($"Transcription response received sessionId={sessionId} httpStatus={(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
        {
            BridgeLogger.Log($"Transcription failed sessionId={sessionId} httpStatus={(int)response.StatusCode}");
            throw new InvalidOperationException(
                $"Power Automate transcription request failed with HTTP {(int)response.StatusCode} {response.StatusCode}."
                + Environment.NewLine
                + responseBody);
        }

        var transcriptionResponse = JsonSerializer.Deserialize<HuddleTranscriptionResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Power Automate returned an empty or invalid JSON response.");

        if (!transcriptionResponse.Success)
        {
            BridgeLogger.Log($"Transcription failed sessionId={sessionId} powerAutomateSuccess=false");
            throw new InvalidOperationException(
                "Power Automate transcription reported failure."
                + Environment.NewLine
                + responseBody);
        }

        if (!string.IsNullOrWhiteSpace(transcriptionResponse.SessionId)
            && !string.Equals(transcriptionResponse.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            BridgeLogger.Log($"Transcription failed sessionId={sessionId} reason=sessionIdMismatch");
            throw new InvalidOperationException(
                $"Power Automate returned transcript for session '{transcriptionResponse.SessionId}', but expected '{sessionId}'.");
        }

        if (string.IsNullOrWhiteSpace(transcriptionResponse.Transcript))
        {
            BridgeLogger.Log($"Transcription failed sessionId={sessionId} reason=emptyTranscript");
            throw new InvalidOperationException("Power Automate returned an empty transcript.");
        }

        var status = string.IsNullOrWhiteSpace(transcriptionResponse.Status)
            ? "Transcription complete."
            : transcriptionResponse.Status;

        if (!string.IsNullOrWhiteSpace(transcriptionResponse.StagingStatus))
        {
            BridgeLogger.Log($"Transcription staging sessionId={sessionId} status=\"{transcriptionResponse.StagingStatus}\"");
        }

        if (!string.IsNullOrWhiteSpace(transcriptionResponse.StagingRecordId))
        {
            BridgeLogger.Log($"Transcription staging sessionId={sessionId} recordId=\"{transcriptionResponse.StagingRecordId}\"");
        }

        BridgeLogger.Log($"Transcription completed sessionId={sessionId} transcriptChars={transcriptionResponse.Transcript.Length}");

        return new HuddleRecordingSendResult(
            true,
            "Transcription completed successfully.",
            audioFilePath,
            sessionId,
            status,
            transcriptionResponse.Transcript ?? "",
            transcriptionResponse.StagingStatus ?? "",
            transcriptionResponse.StagingRecordId ?? "");
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
            flowUrl = ReadFlowUrlFromConfigFile();
        }

        if (string.IsNullOrWhiteSpace(flowUrl))
        {
            throw new InvalidOperationException(
                $"Set {FlowUrlEnvironmentVariable} or configure {AppInfo.UserConfigFilePath} before sending recordings to Huddle.");
        }

        if (!Uri.TryCreate(flowUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{FlowUrlEnvironmentVariable} must be a valid HTTP or HTTPS URL.");
        }

        return flowUrl;
    }

    private static string? ReadFlowUrlFromConfigFile()
    {
        if (!File.Exists(AppInfo.UserConfigFilePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(AppInfo.UserConfigFilePath);
            var config = JsonSerializer.Deserialize<HuddleAudioCaptureConfig>(stream, JsonOptions);
            return config?.HuddleTranscriptionFlowUrl?.Trim();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The Huddle Audio Capture configuration file is not valid JSON: {AppInfo.UserConfigFilePath}",
                ex);
        }
    }
}

sealed record HuddleTranscriptionRequest(
    string SessionId,
    string FileName,
    string Language,
    string Source,
    string AudioBase64);

sealed record HuddleTranscriptionResponse(
    bool Success,
    string? SessionId,
    string? Status,
    string? Transcript,
    string? StagingStatus,
    string? StagingRecordId);

sealed record HuddleAudioCaptureConfig(string? HuddleTranscriptionFlowUrl);

sealed record HuddleRecordingSendResult(
    bool Success,
    string Message,
    string AudioFilePath,
    string SessionId,
    string Status,
    string Transcript,
    string StagingStatus = "",
    string StagingRecordId = "");
