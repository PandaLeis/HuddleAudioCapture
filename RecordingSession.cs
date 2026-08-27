sealed class RecordingSession
{
    public RecordingSession(string audioFilePath, string deviceName)
        : this(Guid.NewGuid().ToString(), audioFilePath, deviceName)
    {
    }

    public RecordingSession(string sessionId, string audioFilePath, string deviceName)
    {
        SessionId = sessionId;
        AudioFilePath = audioFilePath;
        DeviceName = deviceName;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string SessionId { get; }

    public string AudioFilePath { get; }

    public string DeviceName { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? StoppedAt { get; private set; }

    public bool AudibleAudioDetected { get; private set; }

    public TimeSpan Duration => (StoppedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string MetadataFilePath => Path.ChangeExtension(AudioFilePath, ".json");

    public string FileName => Path.GetFileName(AudioFilePath);

    public void Complete(bool audibleAudioDetected)
    {
        AudibleAudioDetected = audibleAudioDetected;
        StoppedAt = DateTimeOffset.UtcNow;
    }
}
