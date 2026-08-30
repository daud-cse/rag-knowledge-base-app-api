using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RagKnowledgeBaseApp.Api.Domain;

namespace RagKnowledgeBaseApp.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "rag-knowledge-base-app";
    public string Audience { get; set; } = "rag-knowledge-base-app-ui";
    /// <summary>Development default. Override via Jwt:SigningKey (or a user secret) in any real deployment.</summary>
    public string SigningKey { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 480;
}

/// <summary>Custom claim names carried in the JWT. In an Entra ID deployment these are the
/// claims you would map from the identity provider token.</summary>
public static class AppClaims
{
    public const string TenantId = "tenant_id";
    public const string TenantName = "tenant_name";
    public const string Department = "department";
    public const string MaxClassification = "max_classification";
    public const string Provider = "idp";
}

public static class Policies
{
    public const string ChatbotAdmin = "ChatbotAdmin";
    public const string KnowledgeAdmin = "KnowledgeAdmin";
    public const string CompanyAdmin = "CompanyAdmin";
    public const string SuperAdmin = "SuperAdmin";
}

public interface ITokenService
{
    (string token, DateTime expiresAt) Issue(User user, Tenant tenant);
}

public class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _key;

    public TokenService(JwtOptions options)
    {
        _options = options;
        var secret = string.IsNullOrWhiteSpace(options.SigningKey)
            ? DevelopmentKey()
            : options.SigningKey;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>Stable per-machine fallback so tokens survive a restart during development.</summary>
    private static string DevelopmentKey()
    {
        var seed = "rag-knowledge-base-app-dev-key:" + Environment.MachineName;
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }

    public SymmetricSecurityKey Key => _key;

    public (string token, DateTime expiresAt) Issue(User user, Tenant tenant)
    {
        var expires = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AppClaims.TenantId, user.TenantId.ToString()),
            new(AppClaims.TenantName, tenant.Name),
            new(AppClaims.MaxClassification, user.MaxClassification.ToString()),
            new(AppClaims.Provider, user.IdentityProvider ?? "local")
        };
        if (!string.IsNullOrWhiteSpace(user.Department))
            claims.Add(new Claim(AppClaims.Department, user.Department));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

/// <summary>Per-request view of the caller. Every controller and the retrieval pipeline read the
/// tenant from here rather than from a request body, so a client cannot cross tenants.</summary>
public class CurrentUser
{
    public CurrentUser(IHttpContextAccessor accessor)
    {
        var p = accessor.HttpContext?.User;
        if (p?.Identity?.IsAuthenticated != true) return;

        IsAuthenticated = true;
        Id = Guid.TryParse(p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : Guid.Empty;
        Email = p.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value ?? "";
        DisplayName = p.FindFirst(ClaimTypes.Name)?.Value ?? "";
        TenantId = Guid.TryParse(p.FindFirst(AppClaims.TenantId)?.Value, out var t) ? t : Guid.Empty;
        TenantName = p.FindFirst(AppClaims.TenantName)?.Value ?? "";
        Department = p.FindFirst(AppClaims.Department)?.Value;
        Role = Enum.TryParse<UserRole>(p.FindFirst(ClaimTypes.Role)?.Value, out var r) ? r : UserRole.User;
        MaxClassification = Enum.TryParse<Classification>(p.FindFirst(AppClaims.MaxClassification)?.Value,
            out var c) ? c : Classification.Internal;
    }

    public bool IsAuthenticated { get; }
    public Guid Id { get; }
    public string Email { get; } = "";
    public string DisplayName { get; } = "";
    public Guid TenantId { get; }
    public string TenantName { get; } = "";
    public string? Department { get; }
    public UserRole Role { get; }
    public Classification MaxClassification { get; }

    public bool IsAtLeast(UserRole role) => Role >= role;
}
