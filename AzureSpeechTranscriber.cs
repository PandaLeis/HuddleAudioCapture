using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System.Text;

sealed class AzureSpeechTranscriber
{
    private readonly string key;
    private readonly string region;
    private readonly string language;

    private AzureSpeechTranscriber(string key, string region, string language)
    {
        this.key = key;
        this.region = region;
        this.language = language;
    }

    public static AzureSpeechTranscriber FromEnvironment(string language)
    {
        var key = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")?.Trim();
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION")?.Trim();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
        {
            throw new ArgumentException("Set AZURE_SPEECH_KEY and AZURE_SPEECH_REGION before using --transcribe.");
        }

        return new AzureSpeechTranscriber(key, region, language);
    }

    public async Task<string> TranscribeWavFileAsync(string wavPath)
    {
        var config = SpeechConfig.FromSubscription(key, region);
        config.SpeechRecognitionLanguage = language;

        using var audioConfig = AudioConfig.FromWavFileInput(wavPath);
        using var recognizer = new SpeechRecognizer(config, audioConfig);

        var transcript = new StringBuilder();
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                transcript.AppendLine(e.Result.Text);
                Console.WriteLine(e.Result.Text);
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                completion.TrySetResult(new InvalidOperationException($"Azure Speech canceled transcription: {e.ErrorDetails}"));
                return;
            }

            completion.TrySetResult(null);
        };

        recognizer.SessionStopped += (_, _) => completion.TrySetResult(null);

        await recognizer.StartContinuousRecognitionAsync();
        var error = await completion.Task;
        await recognizer.StopContinuousRecognitionAsync();

        if (error is not null)
        {
            throw error;
        }

        return transcript.Length == 0
            ? "No speech recognized."
            : transcript.ToString();
    }
}
