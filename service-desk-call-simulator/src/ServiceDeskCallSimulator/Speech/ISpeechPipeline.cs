namespace ServiceDeskCallSimulator.Speech;

/// <summary>
/// A recognition update emitted by the service-desk speech pipeline.
/// </summary>
public sealed record SpeechRecognitionUpdate(
    string? Text,
    bool IsFinal,
    string? Error,
    string? SegmentId = null);

/// <summary>
/// Per-call speech recognition and synthesis boundary.
/// </summary>
public interface ISpeechPipeline : IAsyncDisposable
{
    /// <summary>
    /// Raised for interim, final, and safe fault recognition updates.
    /// </summary>
    event EventHandler<SpeechRecognitionUpdate>? RecognitionUpdated;

    /// <summary>
    /// Starts one continuous recognizer for the selected locale.
    /// </summary>
    Task StartRecognitionAsync(string locale, CancellationToken cancellationToken);

    /// <summary>
    /// Writes validated 16 kHz, 16-bit, mono PCM to the recognizer's push stream.
    /// </summary>
    ValueTask WritePcmAsync(ReadOnlyMemory<byte> pcm16KMono, CancellationToken cancellationToken);

    /// <summary>
    /// Synthesizes text into raw 16 kHz, 16-bit, mono PCM.
    /// </summary>
    Task<byte[]> SynthesizeAsync(string voice, string text, CancellationToken cancellationToken);

    /// <summary>
    /// Stops recognition and closes its native input stream.
    /// </summary>
    Task StopRecognitionAsync();
}

/// <summary>
/// Creates independently disposable speech pipelines for each call.
/// </summary>
public interface ISpeechPipelineFactory
{
    /// <summary>
    /// Creates an unstarted per-call pipeline.
    /// </summary>
    ISpeechPipeline Create();
}
