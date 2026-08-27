namespace ServiceDeskCallSimulator.Azure;

/// <summary>
/// Thrown when the initial Azure authentication probe does not complete inside its deadline.
/// The message is fixed and operator-actionable, so it is safe to surface in the UI.
/// </summary>
public sealed class AzureAuthenticationTimeoutException : TimeoutException
{
    public AzureAuthenticationTimeoutException()
        : this(AzureAuthenticationProbe.DefaultTimeout)
    {
    }

    public AzureAuthenticationTimeoutException(TimeSpan timeout)
        : base(BuildMessage(timeout))
    {
        Timeout = timeout;
    }

    public AzureAuthenticationTimeoutException(string message)
        : base(message)
    {
        Timeout = AzureAuthenticationProbe.DefaultTimeout;
    }

    public AzureAuthenticationTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
        Timeout = AzureAuthenticationProbe.DefaultTimeout;
    }

    /// <summary>
    /// Gets the deadline that elapsed.
    /// </summary>
    public TimeSpan Timeout { get; }

    private static string BuildMessage(TimeSpan timeout) =>
        $"Azure authentication did not complete within {(int)Math.Round(timeout.TotalSeconds)} seconds. "
        + "Sign in with 'az login' (or Visual Studio) and select Retry.";
}

/// <summary>
/// Bounds the initial Azure authentication probe so a stalled credential can never leave the
/// window sitting at "Azure authentication: InProgress" forever.
/// </summary>
/// <remarks>
/// Two independent bounds are applied: the probe receives a linked token that is cancelled at
/// the deadline (cooperative cancellation for credentials that honour it) and the resulting task
/// is additionally awaited through <c>Task.WaitAsync</c>, so a credential that ignores
/// cancellation still cannot block initialization. Either way the caller observes an
/// <see cref="AzureAuthenticationTimeoutException"/>, which the UI renders as an inline Error
/// with Retry rather than hanging.
/// </remarks>
public static class AzureAuthenticationProbe
{
    /// <summary>
    /// The default authentication deadline.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Runs <paramref name="probe"/> under <paramref name="timeout"/>, linked to
    /// <paramref name="cancellationToken"/> (the owning form's lifetime).
    /// </summary>
    /// <exception cref="AzureAuthenticationTimeoutException">The deadline elapsed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, for example because the window closed.
    /// </exception>
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await probe(deadline.Token).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (exception is not AzureAuthenticationTimeoutException)
        {
            throw new AzureAuthenticationTimeoutException(timeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked deadline (not the form lifetime) cancelled the probe.
            throw new AzureAuthenticationTimeoutException(timeout);
        }
    }
}
