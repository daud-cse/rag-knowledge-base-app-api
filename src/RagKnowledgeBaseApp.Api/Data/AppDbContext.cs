using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Domain;

namespace RagKnowledgeBaseApp.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<Chatbot> Chatbots => Set<Chatbot>();
    public DbSet<ChatbotKnowledgeBase> ChatbotKnowledgeBases => Set<ChatbotKnowledgeBase>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            e.HasIndex(x => x.Email);
            e.HasOne(x => x.Tenant).WithMany(t => t.Users).HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Tenant>().HasIndex(x => x.Slug).IsUnique();

        b.Entity<KnowledgeBase>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.Scope });
            e.HasIndex(x => x.OwnerUserId);
        });

        b.Entity<Document>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.KnowledgeBaseId });
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.KnowledgeBase).WithMany(k => k.Documents)
                .HasForeignKey(x => x.KnowledgeBaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DocumentChunk>(e =>
        {
            // Retrieval always filters tenant first, then the candidate knowledge bases.
            e.HasIndex(x => new { x.TenantId, x.KnowledgeBaseId });
            e.HasOne(x => x.Document).WithMany(d => d.Chunks)
                .HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Chatbot>().HasIndex(x => x.TenantId);

        b.Entity<ChatbotKnowledgeBase>(e =>
        {
            e.HasKey(x => new { x.ChatbotId, x.KnowledgeBaseId });
            e.HasOne(x => x.Chatbot).WithMany(c => c.KnowledgeBases)
                .HasForeignKey(x => x.ChatbotId).OnDelete(DeleteBehavior.Cascade);
            // SQL Server rejects two cascade paths into the same join table, so the knowledge-base
            // side is NO ACTION and KnowledgeBasesController removes the mappings explicitly.
            e.HasOne(x => x.KnowledgeBase).WithMany()
                .HasForeignKey(x => x.KnowledgeBaseId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<Conversation>(e =>
        {
            e.HasIndex(x => new { x.TenantId, x.UserId });
            e.HasOne(x => x.Chatbot).WithMany().HasForeignKey(x => x.ChatbotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Message>(e =>
        {
            e.HasIndex(x => x.ConversationId);
            e.HasOne(x => x.Conversation).WithMany(c => c.Messages)
                .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>().HasIndex(x => new { x.TenantId, x.Timestamp });
    }
}
