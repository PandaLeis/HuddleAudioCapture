using System.Text.Json;
using System.Text.Json.Serialization;

static class RecordingMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(RecordingSession session, CancellationToken cancellationToken = default)
    {
        var metadata = new RecordingMetadataDocument(
            session.SessionId,
            session.StartedAt,
            session.StoppedAt,
            session.FileName,
            session.DeviceName,
            Math.Round(session.Duration.TotalSeconds, 2),
            session.AudibleAudioDetected);

        await using var stream = File.Create(session.MetadataFilePath);
        await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken);
    }

    private sealed record RecordingMetadataDocument(
        string SessionId,
        DateTimeOffset RecordedAt,
        DateTimeOffset? StoppedAt,
        string FileName,
        string DeviceName,
        double DurationSeconds,
        bool AudibleAudioDetected);
}
