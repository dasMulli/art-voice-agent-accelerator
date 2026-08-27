using System.Threading.Channels;

namespace ServiceDeskCallSimulator.Media;

/// <summary>
/// Narrow media boundary consumed by the scripted caller conversation.
/// </summary>
public interface ICallMediaTransport
{
    /// <summary>
    /// Completes when media format validation and socket connection succeed.
    /// </summary>
    Task ConnectionReady { get; }

    /// <summary>
    /// Completes when the remote media stream disconnects.
    /// </summary>
    Task Disconnected { get; }

    /// <summary>
    /// Gets the bounded inbound service-desk PCM stream.
    /// </summary>
    ChannelReader<AcsInboundAudioFrame> InboundFrames { get; }

    /// <summary>
    /// Creates a generation used to cancel stale caller playback.
    /// </summary>
    long CreateAudioGeneration();

    /// <summary>
    /// Sends one exact outbound PCM frame.
    /// </summary>
    Task SendAudioAsync(
        long generation,
        ReadOnlyMemory<byte> pcm16KMono,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a caller playback generation and rejects its later frames.
    /// </summary>
    Task StopAudioAsync(long generation, CancellationToken cancellationToken = default);
}
