using NAudio.CoreAudioApi;
using NAudio.Wave;

sealed class LoopbackRecorder : IDisposable
{
    private readonly WasapiLoopbackCapture capture;
    private readonly WaveFileWriter writer;
    private readonly ManualResetEventSlim stopped = new(false);
    private DateTimeOffset lastLevelPrint = DateTimeOffset.MinValue;
    private bool disposed;
    private bool stoppedAndFinalized;
    private Exception? captureError;

    public LoopbackRecorder(MMDevice playbackDevice, string outputPath)
    {
        capture = new WasapiLoopbackCapture(playbackDevice);
        var outputFormat = new WaveFormat(capture.WaveFormat.SampleRate, 16, capture.WaveFormat.Channels);
        writer = new WaveFileWriter(outputPath, outputFormat);

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
    }

    public event Action<float>? InputPeakAvailable;

    public bool DetectedAudio { get; private set; }

    public void Start()
    {
        capture.StartRecording();
    }

    public void Stop()
    {
        if (stoppedAndFinalized)
        {
            return;
        }

        if (capture.CaptureState == CaptureState.Capturing)
        {
            capture.StopRecording();
            if (!stopped.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting for audio capture to stop.");
            }
        }

        writer.Dispose();
        stoppedAndFinalized = true;

        if (captureError is not null)
        {
            throw new InvalidOperationException("Recording stopped because of an error.", captureError);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        capture.Dispose();
        stopped.Dispose();
        disposed = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var peak = WritePcm16(writer, capture.WaveFormat, e.Buffer, e.BytesRecorded);

        if (peak > 0.01f)
        {
            DetectedAudio = true;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastLevelPrint > TimeSpan.FromSeconds(1))
        {
            InputPeakAvailable?.Invoke(peak);
            lastLevelPrint = now;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        captureError = e.Exception;
        stopped.Set();
    }

    private static float WritePcm16(WaveFileWriter writer, WaveFormat inputFormat, byte[] buffer, int bytesRecorded)
    {
        if (inputFormat.Encoding == WaveFormatEncoding.IeeeFloat && inputFormat.BitsPerSample == 32)
        {
            var output = new byte[bytesRecorded / 2];
            var outputIndex = 0;
            var peak = 0f;

            for (var inputIndex = 0; inputIndex <= bytesRecorded - 4; inputIndex += 4)
            {
                var sample = Math.Clamp(BitConverter.ToSingle(buffer, inputIndex), -1f, 1f);
                peak = Math.Max(peak, Math.Abs(sample));

                var pcm = (short)Math.Round(sample * short.MaxValue);
                output[outputIndex++] = (byte)(pcm & 0xff);
                output[outputIndex++] = (byte)((pcm >> 8) & 0xff);
            }

            writer.Write(output, 0, outputIndex);
            return peak;
        }

        if (inputFormat.Encoding == WaveFormatEncoding.Pcm && inputFormat.BitsPerSample == 16)
        {
            writer.Write(buffer, 0, bytesRecorded);

            var peak = 0f;
            for (var inputIndex = 0; inputIndex <= bytesRecorded - 2; inputIndex += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, inputIndex) / (float)short.MaxValue));
            }

            return peak;
        }

        throw new NotSupportedException($"Unsupported capture format: {inputFormat.Encoding}, {inputFormat.BitsPerSample}-bit");
    }
}
