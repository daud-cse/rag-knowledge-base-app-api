using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;
using RagKnowledgeBaseApp.Api.Services.Storage;
using RagKnowledgeBaseApp.Api.Services.Vector;

namespace RagKnowledgeBaseApp.Api.Controllers;

[ApiController]
[Route("api/tenants")]
[Authorize(Policy = Policies.SuperAdmin)]
public class TenantsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly IDocumentStorage _storage;
    private readonly IVectorStore _vectors;
    private readonly ITenantSlugResolver _slugs;

    public TenantsController(AppDbContext db, AuditService audit, IDocumentStorage storage,
        IVectorStore vectors, ITenantSlugResolver slugs)
    {
        _db = db;
        _audit = audit;
        _storage = storage;
        _vectors = vectors;
        _slugs = slugs;
    }

    [HttpGet]
    public async Task<ActionResult<TenantDto[]>> List(CancellationToken ct)
    {
        var tenants = await _db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var userCounts = await _db.Users.GroupBy(u => u.TenantId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var botCounts = await _db.Chatbots.GroupBy(c => c.TenantId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var kbCounts = await _db.KnowledgeBases.Where(k => k.Scope == KnowledgeBaseScope.Company)
            .GroupBy(k => k.TenantId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return Ok(tenants.Select(t => new TenantDto(t.Id, t.Name, t.Slug, t.Description,
            t.AllowedEmailDomains, t.Type.ToString(), t.IsActive, t.CreatedAt, userCounts.GetValueOrDefault(t.Id),
            botCounts.GetValueOrDefault(t.Id), kbCounts.GetValueOrDefault(t.Id))).ToArray());
    }

    [HttpPost]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var slug = (request.Slug ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(slug))
            return BadRequest(new { message = "Name and slug are required." });
        if (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
            return Conflict(new { message = $"A company with the slug '{slug}' already exists." });

        var tenant = new Tenant
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Description = request.Description,
            AllowedEmailDomains = NormaliseDomains(request.AllowedEmailDomains)
        };
        _db.Tenants.Add(tenant);

        if (!string.IsNullOrWhiteSpace(request.AdminEmail) && !string.IsNullOrWhiteSpace(request.AdminPassword))
        {
            _db.Users.Add(new User
            {
                TenantId = tenant.Id,
                Email = request.AdminEmail.Trim().ToLowerInvariant(),
                DisplayName = string.IsNullOrWhiteSpace(request.AdminDisplayName)
                    ? request.AdminEmail : request.AdminDisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.AdminPassword),
                Role = UserRole.CompanyAdmin,
                MaxClassification = Classification.Restricted
            });
        }

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tenant.create", "Tenant", tenant.Id.ToString(), new { tenant.Name, tenant.Slug }, ct);

        return Ok(new TenantDto(tenant.Id, tenant.Name, tenant.Slug, tenant.Description,
            tenant.AllowedEmailDomains, tenant.Type.ToString(), tenant.IsActive, tenant.CreatedAt, 0, 0, 0));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, CreateTenantRequest request, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(request.Name)) tenant.Name = request.Name.Trim();
        tenant.Description = request.Description;

        // A personal workspace must never claim an email domain: that would silently pull other
        // people into one person's private space.
        tenant.AllowedEmailDomains = tenant.Type == TenantType.Personal
            ? null : NormaliseDomains(request.AllowedEmailDomains);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tenant.update", "Tenant", id.ToString(), ct: ct);
        return NoContent();
    }

    /// <summary>Domains decide which company an SSO user is placed into, so they are stored
    /// lower-case and stripped of any leading @ or scheme a user might paste in.</summary>
    private static string? NormaliseDomains(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var domains = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant())
            .Where(d => d.Contains('.'))
            .Distinct();
        var joined = string.Join(",", domains);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();
        tenant.IsActive = !tenant.IsActive;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("tenant.toggle", "Tenant", id.ToString(), new { tenant.IsActive }, ct);
        return Ok(new { tenant.IsActive });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        // Deleting the row cascades every table, but the documents and the vector index live
        // outside the database. Offboarding has to remove those too, or a deleted customer's
        // content stays readable in the storage account and searchable in the index.
        var documentCount = await _db.Documents.CountAsync(d => d.TenantId == id, ct);
        await _vectors.DeleteByTenantAsync(id, ct);
        await _storage.DeleteTenantAsync(id, ct);

        // The join table points at knowledge bases with NO ACTION on that side (see AppDbContext).
        await _db.ChatbotKnowledgeBases
            .Where(m => _db.KnowledgeBases.Any(k => k.Id == m.KnowledgeBaseId && k.TenantId == id))
            .ExecuteDeleteAsync(ct);

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync(ct);
        _slugs.Forget(id);

        await _audit.LogAsync("tenant.delete", "Tenant", id.ToString(),
            new { tenant.Name, tenant.Slug, Type = tenant.Type.ToString(), Documents = documentCount }, ct);
        return NoContent();
    }
}
