interface IHuddleRecordingSender
{
    Task<HuddleRecordingSendResult> SendAsync(
        string audioFilePath,
        string sessionId,
        CancellationToken cancellationToken = default);
}

sealed class HuddleRecordingSender : IHuddleRecordingSender
{
    public Task<HuddleRecordingSendResult> SendAsync(
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

        return Task.FromResult(new HuddleRecordingSendResult(
            "Recording ready for Huddle integration.",
            audioFilePath,
            sessionId));
    }
}

sealed record HuddleRecordingSendResult(string Message, string AudioFilePath, string SessionId);
