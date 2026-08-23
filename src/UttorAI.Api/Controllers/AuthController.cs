using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using UttorAI.Api.Auth;
using UttorAI.Api.Data;
using UttorAI.Api.Domain;
using UttorAI.Api.Dtos;
using UttorAI.Api.Services;

namespace UttorAI.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITokenService _tokens;
    private readonly AuditService _audit;
    private readonly CurrentUser _current;
    private readonly ExternalAuthOptions _external;
    private readonly IExternalTokenValidator _validator;
    private readonly WorkspaceProvisioner _provisioner;

    public AuthController(AppDbContext db, ITokenService tokens, AuditService audit,
        CurrentUser current, ExternalAuthOptions external, IExternalTokenValidator validator,
        WorkspaceProvisioner provisioner)
    {
        _provisioner = provisioner;
        _db = db;
        _tokens = tokens;
        _audit = audit;
        _current = current;
        _external = external;
        _validator = validator;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        var user = await _db.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        // Same response for unknown user and wrong password, so the endpoint cannot enumerate accounts.
        if (user is null || !user.IsActive || string.IsNullOrEmpty(user.PasswordHash) ||
            !BCrypt.Net.BCrypt.Verify(request.Password ?? "", user.PasswordHash))
            return Unauthorized(new { message = "Invalid email or password." });

        if (user.Tenant is null || !user.Tenant.IsActive)
            return Unauthorized(new { message = "This company account is disabled." });

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var (token, expires) = _tokens.Issue(user, user.Tenant);
        var me = Describe(user, user.Tenant);

        _db.AuditLogs.Add(new Domain.AuditLog
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            UserEmail = user.Email,
            Action = "auth.login",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);

        return Ok(new LoginResponse(token, expires, me));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
    {
        var user = await _db.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Id == _current.Id, ct);
        if (user?.Tenant is null) return Unauthorized();
        return Ok(Describe(user, user.Tenant));
    }

    /// <summary>Advertises which identity providers are wired up, and the public client ids the UI
    /// needs to start each flow. Nothing secret is returned.</summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public ActionResult<object> Providers() => Ok(new
    {
        local = true,
        individualSignup = _external.AllowIndividualSignup,
        companySignup = _external.AllowCompanySignup,
        google = _external.Google.Enabled,
        googleClientId = _external.Google.Enabled ? _external.Google.ClientId : null,
        entraId = _external.EntraId.Enabled,
        entraClientId = _external.EntraId.Enabled ? _external.EntraId.ClientId : null,
        entraAuthority = _external.EntraId.Enabled ? _external.EntraId.Authority : null,
        saml = _external.Saml.Enabled
    });

    /// <summary>Exchanges a verified Google or Microsoft ID token for an application session.
    ///
    /// The external token only proves who the person is. Which company they belong to, what role
    /// they get and what they may read stay entirely under this application's control.</summary>
    [HttpPost("external")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> External(ExternalLoginRequest request,
        CancellationToken ct)
    {
        ExternalIdentity identity;
        try
        {
            identity = await _validator.ValidateAsync(request.Provider ?? "", request.IdToken ?? "", ct);
        }
        catch (SecurityTokenException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }

        if (!identity.EmailVerified)
            return Unauthorized(new { message = "That account's email address is not verified." });

        var user = await _db.Users.Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.Email == identity.Email, ct);

        if (user is null)
        {
            // Two ways an unknown person can be admitted:
            //   1. Their email domain belongs to a company, so they join that company as an employee.
            //   2. Otherwise they are an individual, and get their own personal workspace.
            var domain = identity.Email.Split('@').Last();
            var tenant = await FindTenantForDomainAsync(domain, ct);

            if (tenant is not null)
            {
                user = new User
                {
                    TenantId = tenant.Id,
                    Email = identity.Email,
                    DisplayName = identity.DisplayName,
                    Role = Enum.TryParse<UserRole>(_external.AutoProvisionRole, out var role)
                        ? role : UserRole.User,
                    // Someone joining by domain match starts at the lowest clearance, whatever
                    // their role: an administrator decides what they may actually read.
                    MaxClassification = Classification.Internal,
                    ExternalId = identity.Subject,
                    IdentityProvider = identity.Provider,
                    PasswordHash = null
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync(ct);
                user.Tenant = tenant;

                await WriteAuditAsync(tenant.Id, user, "auth.join-company",
                    new { identity.Provider, Domain = domain }, ct);
            }
            else
            {
                if (!_external.AllowIndividualSignup)
                    return Unauthorized(new
                    {
                        message = $"{identity.Email} is not registered. Ask an administrator to " +
                                  $"create the account, or to add '{domain}' to the company's " +
                                  $"allowed email domains."
                    });

                var (personal, owner) = await _provisioner.CreatePersonalWorkspaceAsync(
                    identity.Email, identity.DisplayName, passwordHash: null,
                    identity.Subject, identity.Provider, ct);

                user = owner;
                user.Tenant = personal;

                await WriteAuditAsync(personal.Id, user, "auth.create-personal-workspace",
                    new { identity.Provider }, ct);
            }
        }
        else
        {
            if (!user.IsActive)
                return Unauthorized(new { message = "This account is disabled." });

            // First SSO sign-in for an account created locally links the two identities.
            user.ExternalId ??= identity.Subject;
            user.IdentityProvider = identity.Provider;
        }

        if (user.Tenant is null || !user.Tenant.IsActive)
            return Unauthorized(new { message = "This company account is disabled." });

        user.LastLoginAt = DateTime.UtcNow;
        _db.AuditLogs.Add(new Domain.AuditLog
        {
            TenantId = user.TenantId, UserId = user.Id, UserEmail = user.Email,
            Action = "auth.login.sso",
            Details = JsonSerializer.Serialize(new { identity.Provider }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);

        var (token, expires) = _tokens.Issue(user, user.Tenant);
        return Ok(new LoginResponse(token, expires, Describe(user, user.Tenant)));
    }

    /// <summary>Sign up with an email and password, as either an individual or a company.
    /// Individuals get a personal workspace; a company gets an organisation plus its first
    /// administrator.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Register(RegisterRequest request,
        CancellationToken ct)
    {
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest(new { message = "A valid email address is required." });
        if ((request.Password ?? "").Length < 8)
            return BadRequest(new { message = "Password must be at least 8 characters." });

        var wantsCompany = string.Equals(request.AccountType, "Company", StringComparison.OrdinalIgnoreCase);

        if (wantsCompany && !_external.AllowCompanySignup)
            return Forbid();
        if (!wantsCompany && !_external.AllowIndividualSignup)
            return Forbid();

        // One account per email address across the whole platform, so a later SSO sign-in for the
        // same address resolves to exactly one workspace.
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { message = "An account with that email already exists. Sign in instead." });

        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? email.Split('@')[0] : request.DisplayName.Trim();

        Tenant tenant;
        User user;
        try
        {
            if (wantsCompany)
            {
                if (string.IsNullOrWhiteSpace(request.CompanyName))
                    return BadRequest(new { message = "Company name is required." });

                (tenant, user) = await _provisioner.CreateCompanyWorkspaceAsync(
                    request.CompanyName, request.Slug, email, displayName, hash,
                    externalId: null, identityProvider: null,
                    allowedEmailDomains: request.AllowedEmailDomains, ct);
            }
            else
            {
                (tenant, user) = await _provisioner.CreatePersonalWorkspaceAsync(
                    email, displayName, hash, externalId: null, identityProvider: null, ct);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        await WriteAuditAsync(tenant.Id, user,
            wantsCompany ? "auth.register-company" : "auth.register-personal",
            new { Type = tenant.Type.ToString() }, ct);

        var (token, expires) = _tokens.Issue(user, tenant);
        return Ok(new LoginResponse(token, expires, Describe(user, tenant)));
    }

    private async Task WriteAuditAsync(Guid tenantId, User user, string action, object? details,
        CancellationToken ct)
    {
        _db.AuditLogs.Add(new Domain.AuditLog
        {
            TenantId = tenantId,
            UserId = user.Id,
            UserEmail = user.Email,
            Action = action,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);
    }

    private static MeDto Describe(User user, Tenant tenant) => new(
        user.Id, user.Email, user.DisplayName, user.Role.ToString(), tenant.Name, user.TenantId,
        user.MaxClassification.ToString(), user.Department, user.IdentityProvider ?? "local",
        tenant.Type.ToString());

    /// <summary>Only company workspaces claim email domains. A personal workspace never does, so an
    /// individual signing up can never be absorbed into someone else's private space.</summary>
    private async Task<Tenant?> FindTenantForDomainAsync(string domain, CancellationToken ct)
    {
        var candidates = await _db.Tenants
            .Where(t => t.IsActive && t.Type == TenantType.Company
                        && t.AllowedEmailDomains != null && t.AllowedEmailDomains != "")
            .ToListAsync(ct);

        return candidates.FirstOrDefault(t => t.AllowedEmailDomains!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        // Tokens are stateless; the audit entry is the point of this endpoint.
        await _audit.LogAsync("auth.logout", ct: ct);
        return NoContent();
    }
}
