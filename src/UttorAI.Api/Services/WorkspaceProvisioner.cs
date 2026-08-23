using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UttorAI.Api.Data;
using UttorAI.Api.Domain;

namespace UttorAI.Api.Services;

/// <summary>Creates the two kinds of workspace the product supports.
///
/// A <b>company</b> workspace is an organisation: an administrator adds employees, chatbots and
/// shared knowledge bases. A <b>personal</b> workspace belongs to exactly one person and is created
/// automatically the first time an individual signs in.
///
/// Both are the same <see cref="Tenant"/> row with a different <see cref="TenantType"/>, which is
/// what keeps retrieval, storage and authorisation identical for both. Nothing downstream needs to
/// know which kind it is dealing with.</summary>
public class WorkspaceProvisioner
{
    private readonly AppDbContext _db;
    private readonly ILogger<WorkspaceProvisioner> _logger;

    public WorkspaceProvisioner(AppDbContext db, ILogger<WorkspaceProvisioner> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ------------------------------ personal ------------------------------

    /// <summary>Creates a one-person workspace, with a starter chatbot and knowledge base so the
    /// app is usable the moment the person lands in it.</summary>
    public async Task<(Tenant tenant, User user)> CreatePersonalWorkspaceAsync(string email,
        string displayName, string? passwordHash, string? externalId, string? identityProvider,
        CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim();

        var tenant = new Tenant
        {
            Name = name,
            Slug = await UniqueSlugAsync(name, ct),
            Type = TenantType.Personal,
            Description = "Personal workspace.",
            // A personal workspace must never absorb other people by email domain.
            AllowedEmailDomains = null
        };

        var user = new User
        {
            TenantId = tenant.Id,
            Email = email,
            DisplayName = name,
            // Owner of their own workspace: they can create chatbots and knowledge bases in it.
            // It grants nothing anywhere else, because every query is scoped to this tenant.
            Role = UserRole.CompanyAdmin,
            MaxClassification = Classification.Restricted,
            PasswordHash = passwordHash,
            ExternalId = externalId,
            IdentityProvider = identityProvider
        };

        tenant.OwnerUserId = user.Id;
        _db.Tenants.Add(tenant);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        await AddStarterWorkspaceAsync(tenant, "My Knowledge",
            "Documents you upload to your personal workspace.",
            "My Assistant",
            "Answers questions from the documents in your personal workspace.",
            "Hi! Upload a document, then ask me anything about it.", ct);

        _logger.LogInformation("Created personal workspace {Slug} for {Email}", tenant.Slug, email);
        return (tenant, user);
    }

    // ------------------------------ company -------------------------------

    /// <summary>Creates an organisation and its first administrator.</summary>
    public async Task<(Tenant tenant, User admin)> CreateCompanyWorkspaceAsync(string companyName,
        string? requestedSlug, string adminEmail, string adminDisplayName, string? passwordHash,
        string? externalId, string? identityProvider, string? allowedEmailDomains,
        CancellationToken ct = default)
    {
        var slug = string.IsNullOrWhiteSpace(requestedSlug)
            ? await UniqueSlugAsync(companyName, ct)
            : Slugify(requestedSlug);

        if (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
            throw new InvalidOperationException($"A workspace with the address '{slug}' already exists.");

        var tenant = new Tenant
        {
            Name = companyName.Trim(),
            Slug = slug,
            Type = TenantType.Company,
            AllowedEmailDomains = allowedEmailDomains
        };

        var admin = new User
        {
            TenantId = tenant.Id,
            Email = adminEmail,
            DisplayName = string.IsNullOrWhiteSpace(adminDisplayName) ? adminEmail : adminDisplayName,
            Role = UserRole.CompanyAdmin,
            MaxClassification = Classification.Restricted,
            PasswordHash = passwordHash,
            ExternalId = externalId,
            IdentityProvider = identityProvider
        };

        _db.Tenants.Add(tenant);
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(ct);

        await AddStarterWorkspaceAsync(tenant, "Company Knowledge",
            "Shared documents for everyone in this company.",
            "Company Assistant",
            "Answers questions from the company knowledge base, with citations.",
            $"Welcome to {tenant.Name}. Ask me anything about our documents.", ct);

        _logger.LogInformation("Created company workspace {Slug} for {Email}", tenant.Slug, adminEmail);
        return (tenant, admin);
    }

    // ------------------------------- shared -------------------------------

    /// <summary>A brand new workspace with no chatbot is a dead end, so every workspace starts with
    /// one knowledge base and one chatbot already wired together.</summary>
    private async Task AddStarterWorkspaceAsync(Tenant tenant, string kbName, string kbDescription,
        string botName, string botDescription, string welcome, CancellationToken ct)
    {
        var kb = new KnowledgeBase
        {
            TenantId = tenant.Id,
            Name = kbName,
            Description = kbDescription,
            Scope = KnowledgeBaseScope.Company
        };

        var bot = new Chatbot
        {
            TenantId = tenant.Id,
            Name = botName,
            Description = botDescription,
            WelcomeMessage = welcome,
            SystemPrompt = "You are a helpful assistant. Answer using only the supplied context and " +
                           "always cite your sources. If the context does not contain the answer, " +
                           "say so plainly.",
            SuggestedQuestions = "What documents do you have access to?\nSummarise the key points."
        };

        _db.KnowledgeBases.Add(kb);
        _db.Chatbots.Add(bot);
        await _db.SaveChangesAsync(ct);

        _db.ChatbotKnowledgeBases.Add(new ChatbotKnowledgeBase
        {
            ChatbotId = bot.Id,
            KnowledgeBaseId = kb.Id,
            Priority = 1
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The slug names the blob container and appears in operational tooling, so it has to
    /// be unique and URL safe. It is assigned once and never changed.</summary>
    private async Task<string> UniqueSlugAsync(string source, CancellationToken ct)
    {
        var baseSlug = Slugify(source);
        if (baseSlug.Length < 3) baseSlug = $"ws-{baseSlug}".Trim('-');

        var slug = baseSlug;
        var attempt = 1;
        while (await _db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            // Personal workspaces collide constantly (two people called John Smith), so fall back
            // to a short random suffix rather than an ever-growing counter.
            slug = attempt <= 3
                ? $"{baseSlug}-{attempt}"
                : $"{baseSlug}-{Guid.NewGuid():N}"[..Math.Min(baseSlug.Length + 9, 60)];
            attempt++;
        }
        return slug;
    }

    public static string Slugify(string source)
    {
        var lower = (source ?? "").Trim().ToLowerInvariant();
        var cleaned = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Length <= 50 ? cleaned : cleaned[..50].Trim('-');
    }
}
