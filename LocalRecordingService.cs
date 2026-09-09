using NAudio.CoreAudioApi;

sealed class LocalRecordingService : IDisposable
{
    private readonly object sync = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Dictionary<string, RecordingSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHuddleRecordingSender huddleSender = new HuddleRecordingSender();
    private LoopbackRecorder? activeRecorder;
    private RecordingSession? activeSession;
    private float currentPeak;
    private bool disposed;

    public event Action<float>? PeakAvailable;
    public event Action? StateChanged;

    public string Status
    {
        get
        {
            lock (sync)
            {
                return activeSession is null ? "ready" : "recording";
            }
        }
    }

    public RecordingSession? ActiveSession
    {
        get
        {
            lock (sync)
            {
                return activeSession;
            }
        }
    }

    public float CurrentPeak
    {
        get
        {
            lock (sync)
            {
                return currentPeak;
            }
        }
    }

    public IReadOnlyList<MMDevice> GetPlaybackDevices()
    {
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public MMDevice GetDefaultPlaybackDevice()
    {
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    public void CleanupStaleFiles()
    {
        Directory.CreateDirectory(AppInfo.TempFolder);
        var cutoff = DateTimeOffset.Now.AddHours(-24);

        foreach (var file in Directory.EnumerateFiles(AppInfo.TempFolder, "HuddleRecording_*.*"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff.LocalDateTime)
                {
                    info.Delete();
                    BridgeLogger.Log($"Deleted stale temp file {info.Name}");
                }
            }
            catch (Exception ex)
            {
                BridgeLogger.Log($"Failed to delete stale temp file: {ex.Message}");
            }
        }
    }

    public RecordingStartResult Start(string sessionId, MMDevice? playbackDevice = null)
    {
        ValidateSessionId(sessionId);

        lock (sync)
        {
            if (activeSession is not null)
            {
                throw new RecordingConflictException("A recording is already active.");
            }

            Directory.CreateDirectory(AppInfo.TempFolder);
            playbackDevice ??= GetDefaultPlaybackDevice();

            var safeSessionId = sessionId.Trim();
            var wavPath = Path.Combine(AppInfo.TempFolder, $"HuddleRecording_{safeSessionId}.wav");
            var session = new RecordingSession(safeSessionId, wavPath, playbackDevice.FriendlyName);
            var recorder = new LoopbackRecorder(playbackDevice, wavPath);
            recorder.InputPeakAvailable += OnPeakAvailable;
            recorder.Start();

            activeSession = session;
            activeRecorder = recorder;
            sessions[session.SessionId] = session;
            currentPeak = 0;

            BridgeLogger.Log($"Recording start sessionId={session.SessionId} device=\"{session.DeviceName}\"");
            StateChanged?.Invoke();

            return new RecordingStartResult(true, session.SessionId, "recording");
        }
    }

    public async Task<RecordingStopResult> StopAsync(string sessionId)
    {
        LoopbackRecorder recorder;
        RecordingSession session;

        lock (sync)
        {
            ValidateSessionId(sessionId);

            if (activeSession is null || activeRecorder is null)
            {
                throw new InvalidOperationException("No recording is active.");
            }

            if (!activeSession.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The requested session is not the active recording.");
            }

            recorder = activeRecorder;
            session = activeSession;
        }

        await Task.Run(recorder.Stop);
        session.Complete(recorder.DetectedAudio);

        var fileInfo = new FileInfo(session.AudioFilePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The recording file is empty or was not created.");
        }

        await RecordingMetadata.WriteAsync(session);
        recorder.Dispose();

        lock (sync)
        {
            activeRecorder = null;
            activeSession = null;
            currentPeak = 0;
            sessions[session.SessionId] = session;
        }

        BridgeLogger.Log($"Recording stop sessionId={session.SessionId} durationMs={(long)session.Duration.TotalMilliseconds} bytes={fileInfo.Length}");
        BridgeLogger.Log($"Recording finalized sessionId={session.SessionId} audioReady=true audible={session.AudibleAudioDetected}");
        StateChanged?.Invoke();

        return new RecordingStopResult(
            true,
            session.SessionId,
            "ready",
            (long)session.Duration.TotalMilliseconds,
            session.AudibleAudioDetected);
    }

    public async Task<HuddleRecordingSendResult> SubmitForTranscriptionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        RecordingSession session;

        lock (sync)
        {
            session = GetCompletedSessionUnsafe(sessionId);

            if (!session.AudibleAudioDetected)
            {
                throw new InvalidOperationException("No audible audio was detected. Record audio before sending to Huddle.");
            }

            session.MarkTranscriptionStarted();
        }

        BridgeLogger.Log($"Recording ready for transcription sessionId={session.SessionId}");
        StateChanged?.Invoke();

        try
        {
            var result = await huddleSender.SendAsync(session.AudioFilePath, session.SessionId, cancellationToken);

            lock (sync)
            {
                session.MarkTranscriptionComplete(result.Status, result.Transcript);
            }

            await RecordingMetadata.WriteAsync(session, cancellationToken);
            StateChanged?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                session.MarkTranscriptionFailed(ex.Message);
            }

            BridgeLogger.Log($"Transcription failed sessionId={session.SessionId} error=\"{ex.Message}\"");
            await RecordingMetadata.WriteAsync(session, cancellationToken);
            StateChanged?.Invoke();
            throw;
        }
    }

    public RecordingStatusResult GetStatus(string sessionId)
    {
        ValidateSessionId(sessionId);

        lock (sync)
        {
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                throw new FileNotFoundException("Recording session was not found.");
            }

            var status = activeSession?.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase) == true
                ? "recording"
                : session.StoppedAt is null ? "unknown" : "ready";

            return new RecordingStatusResult(
                session.SessionId,
                status,
                (long)session.Duration.TotalMilliseconds,
                currentPeak,
                session.StoppedAt is not null,
                session.AudibleAudioDetected);
        }
    }

    public RecordingSession GetCompletedSession(string sessionId)
    {
        ValidateSessionId(sessionId);

        lock (sync)
        {
            return GetCompletedSessionUnsafe(sessionId);
        }
    }

    public bool Delete(string sessionId)
    {
        var session = GetCompletedSession(sessionId);

        foreach (var path in new[] { session.AudioFilePath, session.MetadataFilePath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        lock (sync)
        {
            sessions.Remove(sessionId);
        }

        BridgeLogger.Log($"Deleted recording sessionId={sessionId}");
        StateChanged?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        activeRecorder?.Dispose();
        enumerator.Dispose();
        disposed = true;
    }

    private void OnPeakAvailable(float peak)
    {
        lock (sync)
        {
            currentPeak = peak;
        }

        PeakAvailable?.Invoke(peak);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out _))
        {
            throw new ArgumentException("sessionId must be a valid GUID.");
        }
    }

    private RecordingSession GetCompletedSessionUnsafe(string sessionId)
    {
        ValidateSessionId(sessionId);

        if (!sessions.TryGetValue(sessionId, out var session))
        {
            throw new FileNotFoundException("Recording session was not found.");
        }

        if (session.StoppedAt is null)
        {
            throw new InvalidOperationException("Recording is not complete.");
        }

        var fileInfo = new FileInfo(session.AudioFilePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new InvalidOperationException("The recording file is empty or was not created.");
        }

        return session;
    }
}

sealed class RecordingConflictException(string message) : InvalidOperationException(message);

sealed record RecordingStartResult(bool Success, string SessionId, string Status);

sealed record RecordingStopResult(
    bool Success,
    string SessionId,
    string Status,
    long DurationMilliseconds,
    bool HasAudibleAudio);

sealed record RecordingStatusResult(
    string SessionId,
    string Status,
    long DurationMilliseconds,
    float Peak,
    bool AudioReady,
    bool HasAudibleAudio);
