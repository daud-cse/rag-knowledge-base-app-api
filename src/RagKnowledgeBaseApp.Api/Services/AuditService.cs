using System.Text.Json;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;

namespace RagKnowledgeBaseApp.Api.Services;

/// <summary>Append-only trail for section 15. Writes are best effort: an audit failure must not
/// take down the operation being audited, but it is logged.</summary>
public class AuditService
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _user;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppDbContext db, CurrentUser user, IHttpContextAccessor http,
        ILogger<AuditService> logger)
    {
        _db = db;
        _user = user;
        _http = http;
        _logger = logger;
    }

    public async Task LogAsync(string action, string? entityType = null, string? entityId = null,
        object? details = null, CancellationToken ct = default)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = _user.TenantId,
                UserId = _user.IsAuthenticated ? _user.Id : null,
                UserEmail = _user.Email,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details is null ? null : JsonSerializer.Serialize(details),
                IpAddress = _http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
                Timestamp = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write audit entry for {Action}", action);
        }
    }
}
