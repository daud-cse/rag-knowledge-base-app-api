using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Policy = Policies.CompanyAdmin)]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly AuditService _audit;

    public UsersController(AppDbContext db, CurrentUser current, AuditService audit)
    {
        _db = db;
        _current = current;
        _audit = audit;
    }

    /// <summary>A company admin only ever sees their own tenant. A super admin may target another
    /// tenant explicitly, which is the only cross-tenant path in the API.</summary>
    private Guid ScopeTenant(Guid? requested)
        => _current.Role == UserRole.SuperAdmin && requested.HasValue ? requested.Value : _current.TenantId;

    [HttpGet]
    public async Task<ActionResult<UserDto[]>> List([FromQuery] Guid? tenantId, CancellationToken ct)
    {
        var scope = ScopeTenant(tenantId);
        var users = await _db.Users.AsNoTracking().Where(u => u.TenantId == scope)
            .OrderBy(u => u.DisplayName).ToListAsync(ct);
        return Ok(users.Select(Map).ToArray());
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var scope = ScopeTenant(request.TenantId);
        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { message = "Email is required." });
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { message = "A user with that email already exists." });
        if (!Enum.TryParse<UserRole>(request.Role, out var role))
            return BadRequest(new { message = $"Unknown role '{request.Role}'." });

        // Nobody may mint an account more privileged than themselves.
        if (role > _current.Role)
            return Forbid();

        var classification = Enum.TryParse<Classification>(request.MaxClassification, out var c)
            ? c : Classification.Internal;

        var user = new User
        {
            TenantId = scope,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim(),
            Department = request.Department,
            // No password means an invited employee: the account exists, and they activate it by
            // signing in with Google or Microsoft using this address.
            PasswordHash = string.IsNullOrWhiteSpace(request.Password)
                ? null : BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = role,
            MaxClassification = classification
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("user.create", "User", user.Id.ToString(), new { user.Email, Role = role.ToString() }, ct);
        return Ok(Map(user));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        if (user.TenantId != _current.TenantId && _current.Role != UserRole.SuperAdmin) return Forbid();

        if (!string.IsNullOrWhiteSpace(request.DisplayName)) user.DisplayName = request.DisplayName.Trim();
        if (request.Department is not null) user.Department = request.Department;
        if (!string.IsNullOrWhiteSpace(request.Role) && Enum.TryParse<UserRole>(request.Role, out var role))
        {
            if (role > _current.Role) return Forbid();
            user.Role = role;
        }
        if (!string.IsNullOrWhiteSpace(request.MaxClassification) &&
            Enum.TryParse<Classification>(request.MaxClassification, out var c))
            user.MaxClassification = c;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(request.Password))
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("user.update", "User", id.ToString(), ct: ct);
        return Ok(Map(user));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (id == _current.Id) return BadRequest(new { message = "You cannot delete your own account." });
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        if (user.TenantId != _current.TenantId && _current.Role != UserRole.SuperAdmin) return Forbid();

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("user.delete", "User", id.ToString(), new { user.Email }, ct);
        return NoContent();
    }

    private static UserDto Map(User u) => new(u.Id, u.TenantId, u.Email, u.DisplayName, u.Department,
        u.Role.ToString(), u.MaxClassification.ToString(), u.IsActive, u.CreatedAt, u.LastLoginAt);
}
