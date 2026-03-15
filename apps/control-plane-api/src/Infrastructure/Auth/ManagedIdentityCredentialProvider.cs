using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Atlas.ControlPlane.Infrastructure.Auth;

/// <summary>
/// Provides managed identity credentials for Azure SDK clients.
///
/// Two credential paths are available to callers; choose the appropriate one explicitly:
///   <see cref="GetCredential"/>              — user-assigned MI (ACR, Key Vault, AI Foundry)
///   <see cref="GetSystemAssignedCredential"/> — system-assigned MI (Resource Graph, Monitor, Cost)
///
/// There is no runtime env-var switch; callers select the path via method name to keep the
/// binding deterministic and auditable.
/// </summary>
public class ManagedIdentityCredentialProvider
{
    private readonly TokenCredential _credential;
    private readonly TokenCredential _managedIdentityOnlyCredential;
    private readonly TokenCredential _systemAssignedCredential;
    private readonly TokenCredential _systemAssignedManagedIdentityOnly;
    private readonly ManagedIdentityOptions _options;
    private readonly bool _enforceManagedIdentityOnly;

    public ManagedIdentityCredentialProvider(
        IOptions<ManagedIdentityOptions> options,
        IHostEnvironment environment)
    {
        _options = options.Value;
        _enforceManagedIdentityOnly = _options.EnforceManagedIdentityOnly
            ?? (_options.EnforceManagedIdentityOnlyInProduction && environment.IsProduction());

        // Build credential chain: Managed Identity -> Azure CLI (dev) -> Environment (CI)
        var credentialOptions = new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true,

            // Allow MI + CLI + Environment for flexibility
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = false,
            ExcludeEnvironmentCredential = false,

            TenantId = _options.TenantId
        };

        _credential = new DefaultAzureCredential(credentialOptions);

        _managedIdentityOnlyCredential = CreateManagedIdentityCredential(_options);
        _systemAssignedManagedIdentityOnly = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        _systemAssignedCredential = new ChainedTokenCredential(
            new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
            new AzureCliCredential(new AzureCliCredentialOptions { TenantId = _options.TenantId }),
            new EnvironmentCredential());
    }

    /// <summary>
    /// Gets the configured TokenCredential for Azure SDK clients.
    /// Binds to the user-assigned identity when AZURE_CLIENT_ID is set (DefaultAzureCredential behaviour).
    /// </summary>
    public TokenCredential GetCredential()
        => _enforceManagedIdentityOnly ? _managedIdentityOnlyCredential : _credential;

    /// <summary>
    /// Returns a credential that explicitly targets the system-assigned managed identity.
    /// This bypasses AZURE_CLIENT_ID so the token carries the system-assigned MI object ID,
    /// which holds subscription-scope Reader / Monitoring Reader / Cost Management Reader roles
    /// required for Azure Resource Graph, Azure Monitor, and Cost Management discovery.
    /// Falls back to Azure CLI (dev) and then environment credentials for local runs.
    /// </summary>
    public TokenCredential GetSystemAssignedCredential()
        => _enforceManagedIdentityOnly ? _systemAssignedManagedIdentityOnly : _systemAssignedCredential;

    private static ManagedIdentityCredential CreateManagedIdentityCredential(ManagedIdentityOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.UserAssignedClientId))
        {
            return new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(options.UserAssignedClientId));
        }

        var envClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(envClientId))
        {
            return new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(envClientId));
        }

        return new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
    }
}

public class ManagedIdentityOptions
{
    public const string SectionName = "ManagedIdentity";

    /// <summary>
    /// Azure AD Tenant ID (optional, uses default tenant if not specified).
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Optional client ID for a user-assigned managed identity.
    /// </summary>
    public string? UserAssignedClientId { get; set; }

    /// <summary>
    /// Enforce managed-identity-only credentials in production when true (default).
    /// </summary>
    public bool EnforceManagedIdentityOnlyInProduction { get; set; } = true;

    /// <summary>
    /// Explicit override to enforce managed-identity-only credentials in any environment.
    /// </summary>
    public bool? EnforceManagedIdentityOnly { get; set; }
}
