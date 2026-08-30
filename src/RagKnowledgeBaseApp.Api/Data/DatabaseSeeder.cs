using System.Text;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Services.Ingestion;
using RagKnowledgeBaseApp.Api.Services.Storage;

namespace RagKnowledgeBaseApp.Api.Data;

/// <summary>Creates two tenants, the RBAC ladder and a working knowledge base so the application is
/// demonstrable on first run. Seeding is skipped once any tenant exists.</summary>
public class DatabaseSeeder
{
    private const string DemoPassword = "Passw0rd!";

    private readonly AppDbContext _db;
    private readonly IDocumentStorage _storage;
    private readonly IngestionQueue _queue;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(AppDbContext db, IDocumentStorage storage, IngestionQueue queue,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Tenants.AnyAsync(ct)) return;

        var contoso = new Tenant
        {
            Name = "Contoso Health",
            Slug = "contoso",
            Description = "Demo healthcare payer used by the sample claims assistant.",
            AllowedEmailDomains = "contoso.com"
        };
        var northwind = new Tenant
        {
            Name = "Northwind Insurance",
            Slug = "northwind",
            Description = "Second tenant, present to demonstrate isolation between companies.",
            AllowedEmailDomains = "northwind.com"
        };
        _db.Tenants.AddRange(contoso, northwind);

        var superAdmin = NewUser(contoso.Id, "super@ragkb.app", "Platform Super Admin",
            UserRole.SuperAdmin, Classification.Restricted, "Platform");
        var companyAdmin = NewUser(contoso.Id, "admin@contoso.com", "Alex Admin",
            UserRole.CompanyAdmin, Classification.Restricted, "IT");
        var knowledgeAdmin = NewUser(contoso.Id, "knowledge@contoso.com", "Kim Knowledge",
            UserRole.KnowledgeAdmin, Classification.Confidential, "Operations");
        var endUser = NewUser(contoso.Id, "user@contoso.com", "Uma User",
            UserRole.User, Classification.Internal, "Claims");
        var northwindAdmin = NewUser(northwind.Id, "admin@northwind.com", "Nora Northwind",
            UserRole.CompanyAdmin, Classification.Restricted, "IT");
        _db.Users.AddRange(superAdmin, companyAdmin, knowledgeAdmin, endUser, northwindAdmin);

        var claimsKb = new KnowledgeBase
        {
            TenantId = contoso.Id,
            Name = "Healthcare Claims KB",
            Description = "Claims guidelines, provider manual and member benefits."
        };
        var hrKb = new KnowledgeBase
        {
            TenantId = contoso.Id,
            Name = "HR Policies KB",
            Description = "Internal HR policies. Empty until an administrator uploads documents."
        };
        _db.KnowledgeBases.AddRange(claimsKb, hrKb);

        var claimsBot = new Chatbot
        {
            TenantId = contoso.Id,
            Name = "Healthcare Claims Assistant",
            Description = "Answers claims questions from the approved knowledge base, with citations.",
            SystemPrompt = "You are the Contoso Health claims assistant. Answer questions using only " +
                           "the company's approved healthcare claims knowledge base. Always provide " +
                           "citations. If the knowledge base does not cover the question, say so.",
            WelcomeMessage = "Hello! Ask me anything about claims submission, provider requirements or " +
                             "member benefits.",
            SuggestedQuestions = "What is the timely filing limit for a claim?\n" +
                                 "Which fields are required on an 837 professional claim?\n" +
                                 "How do I read a denial code on the 835 remittance?\n" +
                                 "What is the member out-of-pocket maximum?"
        };
        var hrBot = new Chatbot
        {
            TenantId = contoso.Id,
            Name = "Employee Assistant",
            Description = "General internal assistant mapped to the HR knowledge base.",
            SystemPrompt = "You are an internal employee assistant for Contoso Health. Answer from the " +
                           "HR knowledge base and cite your sources.",
            WelcomeMessage = "Hi! I can help with HR policies once documents have been uploaded."
        };
        _db.Chatbots.AddRange(claimsBot, hrBot);

        await _db.SaveChangesAsync(ct);

        _db.ChatbotKnowledgeBases.Add(new ChatbotKnowledgeBase
        {
            ChatbotId = claimsBot.Id, KnowledgeBaseId = claimsKb.Id, Priority = 1
        });
        _db.ChatbotKnowledgeBases.Add(new ChatbotKnowledgeBase
        {
            ChatbotId = hrBot.Id, KnowledgeBaseId = hrKb.Id, Priority = 1
        });
        await _db.SaveChangesAsync(ct);

        foreach (var (fileName, body, classification) in SampleDocuments.All)
            await AddSampleDocumentAsync(claimsKb, knowledgeAdmin.Id, fileName, body, classification, ct);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Seeded demo data. Sign in as admin@contoso.com / {Password} (or super@ragkb.app, " +
            "knowledge@contoso.com, user@contoso.com).", DemoPassword);
    }

    private async Task AddSampleDocumentAsync(KnowledgeBase kb, Guid uploaderId, string fileName,
        string body, Classification classification, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var stream = new MemoryStream(bytes);
        var key = await _storage.SaveAsync(kb.TenantId, kb.Id, fileName, stream, ct);

        var doc = new Document
        {
            TenantId = kb.TenantId,
            KnowledgeBaseId = kb.Id,
            FileName = fileName,
            ContentType = "text/markdown",
            SizeBytes = bytes.Length,
            StorageKey = key,
            Classification = classification,
            UploadedByUserId = uploaderId
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(ct);
        await _queue.EnqueueAsync(doc.Id);
    }

    private static User NewUser(Guid tenantId, string email, string name, UserRole role,
        Classification classification, string department) => new()
    {
        TenantId = tenantId,
        Email = email,
        DisplayName = name,
        Department = department,
        Role = role,
        MaxClassification = classification,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(DemoPassword)
    };
}
