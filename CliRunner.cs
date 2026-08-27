using NAudio.CoreAudioApi;
using System.Globalization;
using System.Runtime.InteropServices;

static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = RecorderOptions.Parse(args);
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

            if (options.ListDevices)
            {
                if (devices.Count == 0)
                {
                    Console.WriteLine("No playback devices are available.");
                    return 1;
                }

                for (var i = 0; i < devices.Count; i++)
                {
                    Console.WriteLine($"{i}: {devices[i].FriendlyName}");
                }

                return 0;
            }

            if (devices.Count == 0)
            {
                Console.Error.WriteLine("No playback devices are available.");
                return 1;
            }

            using var playbackDevice = SelectDevice(enumerator, devices, options.Device);
            var outputPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());

            Console.WriteLine($"Selected playback device: {playbackDevice.FriendlyName}");

            using var recorder = new LoopbackRecorder(playbackDevice, outputPath);
            using var stopRequested = new CancellationTokenSource();
            var startedAt = DateTimeOffset.UtcNow;

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopRequested.Cancel();
            };

            recorder.InputPeakAvailable += peak => Console.WriteLine($"Input peak: {peak:P0}");

            recorder.Start();
            Console.WriteLine("Recording computer audio...");
            Console.WriteLine("Press Enter to stop recording.");

            var stopTasks = new List<Task> { WaitForCancellationAsync(stopRequested.Token) };

            if (!Console.IsInputRedirected || options.Duration is null)
            {
                stopTasks.Add(Task.Run(() => Console.ReadLine()));
            }

            if (options.Duration is not null)
            {
                stopTasks.Add(Task.Delay(options.Duration.Value, stopRequested.Token));
            }

            await Task.WhenAny(stopTasks);
            recorder.Stop();

            var duration = DateTimeOffset.UtcNow - startedAt;
            var fileSize = new FileInfo(outputPath).Length;

            if (!recorder.DetectedAudio)
            {
                Console.WriteLine("No audible signal was detected. Make sure audio is playing through the selected playback device.");
            }

            Console.WriteLine($"Saved WAV file: {outputPath}");
            Console.WriteLine($"Recording duration: {duration:hh\\:mm\\:ss}");
            Console.WriteLine($"File size: {fileSize:N0} bytes");
            Console.WriteLine($"Audible audio detected: {(recorder.DetectedAudio ? "yes" : "no")}");

            if (options.Transcribe)
            {
                var transcriptPath = Path.GetFullPath(options.TranscriptPath ?? Path.ChangeExtension(outputPath, ".txt"));
                Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath) ?? Directory.GetCurrentDirectory());

                Console.WriteLine("Transcribing WAV file...");
                string transcript;
                try
                {
                    var transcriber = AzureSpeechTranscriber.FromEnvironment(options.SpeechLanguage);
                    transcript = await transcriber.TranscribeWavFileAsync(outputPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Transcription failed: {ex.Message}");
                    Console.Error.WriteLine("Verify AZURE_SPEECH_KEY and AZURE_SPEECH_REGION match the same Azure Speech resource.");
                    return 1;
                }

                await File.WriteAllTextAsync(transcriptPath, transcript);
                Console.WriteLine($"Saved transcript file: {transcriptPath}");
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            Console.Error.WriteLine($"No output device is available or it could not be opened: {ex.Message}");
            return 1;
        }
    }

    private static MMDevice SelectDevice(MMDeviceEnumerator enumerator, IReadOnlyList<MMDevice> devices, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        if (int.TryParse(selector, out var deviceIndex))
        {
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
            {
                throw new ArgumentException($"Device index {deviceIndex} is out of range.");
            }

            return devices[deviceIndex];
        }

        var matches = devices
            .Where(device => device.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No playback device matches '{selector}'."),
            _ => throw new ArgumentException($"More than one playback device matches '{selector}'. Use --list-devices and select by index.")
        };
    }

    private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [--output <path>] [--duration <seconds>] [--device <name-or-index>] [--transcribe]");
        Console.WriteLine("  dotnet run -- --list-devices");
    }
}

sealed record RecorderOptions(
    string OutputPath,
    TimeSpan? Duration,
    bool ListDevices,
    string? Device,
    bool Transcribe,
    string? TranscriptPath,
    string SpeechLanguage)
{
    public static RecorderOptions Parse(string[] args)
    {
        var outputPath = "recording.wav";
        TimeSpan? duration = null;
        var listDevices = false;
        string? device = null;
        var transcribe = false;
        string? transcriptPath = null;
        var speechLanguage = "en-US";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    outputPath = RequireValue(args, ref i, "--output");
                    break;
                case "--duration":
                    var secondsValue = RequireValue(args, ref i, "--duration");
                    if (!double.TryParse(secondsValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                    {
                        throw new ArgumentException("--duration must be a positive number of seconds.");
                    }
                    duration = TimeSpan.FromSeconds(seconds);
                    break;
                case "--list-devices":
                    listDevices = true;
                    break;
                case "--device":
                    device = RequireValue(args, ref i, "--device");
                    break;
                case "--transcribe":
                    transcribe = true;
                    break;
                case "--transcript":
                    transcriptPath = RequireValue(args, ref i, "--transcript");
                    transcribe = true;
                    break;
                case "--speech-language":
                    speechLanguage = RequireValue(args, ref i, "--speech-language");
                    break;
                case "--help":
                case "-h":
                    throw new ArgumentException("Audio Loopback Recorder");
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new RecorderOptions(outputPath, duration, listDevices, device, transcribe, transcriptPath, speechLanguage);
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{optionName} requires a value.");
        }

        index++;
        return args[index];
    }
}
