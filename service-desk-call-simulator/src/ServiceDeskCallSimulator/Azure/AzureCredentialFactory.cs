using Azure.Core;
using Azure.Identity;

namespace ServiceDeskCallSimulator.Azure;

/// <summary>
/// Builds the single deterministic local developer credential chain used by every Azure client
/// in the simulator.
/// </summary>
/// <remarks>
/// The simulator is a local operator/demo tool, so the only supported sign-ins are the ones the
/// operator actually performs on the desktop: Azure CLI (<c>az login</c>) first, Visual Studio
/// second, and Azure PowerShell as an optional last fallback. A broad
/// <c>DefaultAzureCredential</c> is deliberately not used: its probe order can attempt slower
/// developer or managed-identity sources before the Azure CLI and stall startup for a long time
/// even when <c>az account get-access-token</c> succeeds immediately. The chain stays
/// passwordless (no secret is ever read or stored) and every process-based source is bounded by
/// <see cref="DeveloperToolProcessTimeout"/>.
/// </remarks>
public static class AzureCredentialFactory
{
    /// <summary>
    /// Upper bound applied to each process-based developer credential (<c>az</c>, <c>pwsh</c>).
    /// A hung or prompting helper process therefore fails fast instead of blocking startup.
    /// </summary>
    public static readonly TimeSpan DeveloperToolProcessTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Returns the ordered credential sources of the local developer chain. Exposed so the
    /// deterministic order can be asserted without ever requesting a token.
    /// </summary>
    public static IReadOnlyList<TokenCredential> CreateLocalDeveloperCredentialSources() =>
    [
        new AzureCliCredential(new AzureCliCredentialOptions
        {
            ProcessTimeout = DeveloperToolProcessTimeout,
        }),
        new VisualStudioCredential(),
        new AzurePowerShellCredential(new AzurePowerShellCredentialOptions
        {
            ProcessTimeout = DeveloperToolProcessTimeout,
        }),
    ];

    /// <summary>
    /// Creates the deterministic local developer credential: Azure CLI first, Visual Studio
    /// second, Azure PowerShell last.
    /// </summary>
    public static TokenCredential CreateLocalDeveloperCredential() =>
        new ChainedTokenCredential([.. CreateLocalDeveloperCredentialSources()]);
}
