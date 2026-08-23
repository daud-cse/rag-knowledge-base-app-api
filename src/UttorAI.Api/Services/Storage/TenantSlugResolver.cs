using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using UttorAI.Api.Data;

namespace UttorAI.Api.Services.Storage;

/// <summary>Maps a tenant id onto its slug, for naming per-tenant blob containers.
///
/// Storage keys keep the tenant *id*, which never changes. The slug is only used for the container
/// name, so the account reads well in Storage Explorer. A tenant's slug is fixed at creation and
/// the API offers no way to change it, so a container name stays valid for the tenant's lifetime.
/// </summary>
public interface ITenantSlugResolver
{
    string? Resolve(Guid tenantId);
    void Forget(Guid tenantId);
}

public class TenantSlugResolver : ITenantSlugResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ConcurrentDictionary<Guid, string?> _cache = new();

    public TenantSlugResolver(IServiceScopeFactory scopes) => _scopes = scopes;

    public string? Resolve(Guid tenantId) => _cache.GetOrAdd(tenantId, id =>
    {
        // Storage is a singleton and this runs on the upload path, so a short-lived scope is
        // created rather than holding a DbContext. Each tenant is looked up at most once.
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Tenants.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => t.Slug)
            .FirstOrDefault();
    });

    public void Forget(Guid tenantId) => _cache.TryRemove(tenantId, out _);
}
