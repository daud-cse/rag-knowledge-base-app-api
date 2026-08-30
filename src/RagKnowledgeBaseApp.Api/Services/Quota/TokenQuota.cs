using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;

namespace RagKnowledgeBaseApp.Api.Services.Quota;

/// <summary>Per-user daily token allowance. Set <see cref="DailyTokensPerUser"/> to 0 to turn the
/// limit off entirely, which is what a self-hosted deployment with its own billing would do.</summary>
public class QuotaOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Tokens a single user may spend per UTC day across embedding and chat combined.</summary>
    public int DailyTokensPerUser { get; set; } = 50_000;

    /// <summary>Roles that are never limited. Administrators reindexing a knowledge base would
    /// otherwise lock themselves out of their own tenant.</summary>
    public string[] ExemptRoles { get; set; } = { "SuperAdmin", "CompanyAdmin" };
}

public record QuotaSnapshot(bool Enabled, long Used, int Limit, long Remaining, bool Exceeded)
{
    public static QuotaSnapshot Unlimited => new(false, 0, 0, long.MaxValue, false);
}

public interface ITokenQuota
{
    Task<QuotaSnapshot> GetAsync(Guid userId, string? role, CancellationToken ct = default);

    /// <summary>Adds usage for today. Never throws on contention: the unique index turns a
    /// concurrent insert into an update on retry.</summary>
    Task RecordAsync(Guid tenantId, Guid userId, long embedding = 0, long prompt = 0,
        long completion = 0, CancellationToken ct = default);
}

public class TokenQuota : ITokenQuota
{
    private readonly AppDbContext _db;
    private readonly QuotaOptions _options;
    private readonly ILogger<TokenQuota> _logger;

    public TokenQuota(AppDbContext db, QuotaOptions options, ILogger<TokenQuota> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task<QuotaSnapshot> GetAsync(Guid userId, string? role, CancellationToken ct = default)
    {
        if (!_options.Enabled || _options.DailyTokensPerUser <= 0)
            return QuotaSnapshot.Unlimited;

        if (role is not null && _options.ExemptRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return QuotaSnapshot.Unlimited;

        var row = await _db.DailyTokenUsage.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.UsageDate == Today, ct);

        var used = row?.TotalTokens ?? 0;
        var limit = _options.DailyTokensPerUser;
        return new QuotaSnapshot(true, used, limit, Math.Max(0, limit - used), used >= limit);
    }

    public async Task RecordAsync(Guid tenantId, Guid userId, long embedding = 0, long prompt = 0,
        long completion = 0, CancellationToken ct = default)
    {
        if (embedding == 0 && prompt == 0 && completion == 0) return;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var row = await _db.DailyTokenUsage
                .FirstOrDefaultAsync(x => x.UserId == userId && x.UsageDate == Today, ct);

            if (row is null)
            {
                _db.DailyTokenUsage.Add(new DailyTokenUsage
                {
                    TenantId = tenantId,
                    UserId = userId,
                    UsageDate = Today,
                    EmbeddingTokens = embedding,
                    PromptTokens = prompt,
                    CompletionTokens = completion,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                row.EmbeddingTokens += embedding;
                row.PromptTokens += prompt;
                row.CompletionTokens += completion;
                row.UpdatedAt = DateTime.UtcNow;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Two requests inserted the first row of the day at once. Drop the losing insert
                // and fall through to the update path on the retry.
                foreach (var entry in _db.ChangeTracker.Entries<DailyTokenUsage>().ToList())
                    entry.State = EntityState.Detached;
            }
        }

        _logger.LogWarning("Could not record token usage for user {UserId}", userId);
    }
}
