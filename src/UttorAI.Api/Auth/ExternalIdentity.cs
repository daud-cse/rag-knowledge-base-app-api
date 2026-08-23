using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace UttorAI.Api.Auth;

public class ExternalAuthOptions
{
    public GoogleOptions Google { get; set; } = new();
    public EntraIdOptions EntraId { get; set; } = new();
    public SamlOptions Saml { get; set; } = new();

    /// <summary>Create an account on first successful SSO sign-in, when the email domain maps to a
    /// company. Off means an administrator must create the account first.</summary>
    public bool AutoProvision { get; set; } = true;

    /// <summary>Role given to an account that joins a company by email-domain match.
    /// Deliberately the lowest rung.</summary>
    public string AutoProvisionRole { get; set; } = "User";

    /// <summary>Allow a person with no company to get their own personal workspace.</summary>
    public bool AllowIndividualSignup { get; set; } = true;

    /// <summary>Allow anyone to create a new company workspace from the sign-up page.</summary>
    public bool AllowCompanySignup { get; set; } = true;

    public class GoogleOptions
    {
        public string ClientId { get; set; } = "";
        public bool Enabled => !string.IsNullOrWhiteSpace(ClientId);
    }

    public class EntraIdOptions
    {
        public string TenantId { get; set; } = "";
        public string ClientId { get; set; } = "";
        public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
        public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";
    }

    public class SamlOptions
    {
        public string EntityId { get; set; } = "";
        public bool Enabled => !string.IsNullOrWhiteSpace(EntityId);
    }
}

/// <summary>Identity extracted from a verified provider token.</summary>
public record ExternalIdentity(string Provider, string Subject, string Email, string DisplayName,
    bool EmailVerified);

public interface IExternalTokenValidator
{
    /// <summary>Verifies the provider's ID token signature, issuer, audience and lifetime, and
    /// returns the identity it asserts. Throws SecurityTokenException if anything fails.</summary>
    Task<ExternalIdentity> ValidateAsync(string provider, string idToken, CancellationToken ct = default);
}

/// <summary>Validates Google and Microsoft ID tokens against the provider's published signing keys.
///
/// The token is verified here rather than trusted from the client: a browser can send any JSON it
/// likes to this API, so the signature check is what makes the asserted email meaningful.
/// </summary>
public class ExternalTokenValidator : IExternalTokenValidator
{
    private const string GoogleMetadata = "https://accounts.google.com/.well-known/openid-configuration";

    private readonly ExternalAuthOptions _options;
    private readonly ILogger<ExternalTokenValidator> _logger;

    // ConfigurationManager caches the discovery document and its JWKS, and refreshes them on a
    // schedule, so signing-key rollover is handled without a restart.
    private readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _metadata = new();
    private readonly object _gate = new();

    public ExternalTokenValidator(ExternalAuthOptions options, ILogger<ExternalTokenValidator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<ExternalIdentity> ValidateAsync(string provider, string idToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            throw new SecurityTokenException("No identity token was supplied.");

        var (metadataUrl, validIssuers, audience) = provider.ToLowerInvariant() switch
        {
            "google" when _options.Google.Enabled => (
                GoogleMetadata,
                new[] { "https://accounts.google.com", "accounts.google.com" },
                _options.Google.ClientId),

            "microsoft" when _options.EntraId.Enabled => (
                $"{_options.EntraId.Authority}/.well-known/openid-configuration",
                new[]
                {
                    $"https://login.microsoftonline.com/{_options.EntraId.TenantId}/v2.0",
                    $"https://sts.windows.net/{_options.EntraId.TenantId}/"
                },
                _options.EntraId.ClientId),

            "google" or "microsoft" => throw new SecurityTokenException(
                $"{provider} sign-in is not configured on this server."),

            _ => throw new SecurityTokenException($"Unknown identity provider '{provider}'.")
        };

        var configuration = await GetMetadataAsync(metadataUrl, ct);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        ClaimsPrincipal principal;
        try
        {
            principal = new JwtSecurityTokenHandler().ValidateToken(idToken, parameters, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Rejected {Provider} identity token: {Reason}", provider, ex.Message);
            throw new SecurityTokenException("The identity token could not be verified.");
        }

        var email = principal.FindFirst(ClaimTypes.Email)?.Value
                    ?? principal.FindFirst("email")?.Value
                    ?? principal.FindFirst("preferred_username")?.Value;
        if (string.IsNullOrWhiteSpace(email))
            throw new SecurityTokenException("The identity token does not contain an email address.");

        var subject = principal.FindFirst("sub")?.Value
                      ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? throw new SecurityTokenException("The identity token has no subject.");

        var name = principal.FindFirst("name")?.Value
                   ?? principal.FindFirst(ClaimTypes.Name)?.Value
                   ?? email;

        // Google states this explicitly; Entra ID only issues tokens for accounts it owns, so an
        // absent claim there is treated as verified.
        var verifiedClaim = principal.FindFirst("email_verified")?.Value;
        var verified = provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(verifiedClaim, "true", StringComparison.OrdinalIgnoreCase);

        return new ExternalIdentity(provider.ToLowerInvariant(), subject,
            email.Trim().ToLowerInvariant(), name, verified);
    }

    private Task<OpenIdConnectConfiguration> GetMetadataAsync(string url, CancellationToken ct)
    {
        ConfigurationManager<OpenIdConnectConfiguration> manager;
        lock (_gate)
        {
            if (!_metadata.TryGetValue(url, out manager!))
            {
                manager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    url, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever());
                _metadata[url] = manager;
            }
        }
        return manager.GetConfigurationAsync(ct);
    }
}
