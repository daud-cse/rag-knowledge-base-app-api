using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Services;
using RagKnowledgeBaseApp.Api.Services.Ingestion;
using RagKnowledgeBaseApp.Api.Services.Llm;
using RagKnowledgeBaseApp.Api.Services.Storage;
using RagKnowledgeBaseApp.Api.Services.Vector;

var builder = WebApplication.CreateBuilder(args);

// ---------------- configuration objects ----------------
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
var vectorOptions = builder.Configuration.GetSection("VectorStore").Get<VectorStoreOptions>()
                    ?? new VectorStoreOptions();
var externalAuth = builder.Configuration.GetSection("Authentication").Get<ExternalAuthOptions>()
                   ?? new ExternalAuthOptions();

// Aliases for the flatter section names used elsewhere in this codebase, so either shape works.
// A Google client id is public by design (the browser sends it to Google), so unlike the LLM key
// it is fine in appsettings.json.
externalAuth.Google.ClientId = FirstNonEmpty(
    externalAuth.Google.ClientId,
    builder.Configuration["GoogleAuth:ClientId"]);

externalAuth.EntraId.TenantId = FirstNonEmpty(
    externalAuth.EntraId.TenantId,
    builder.Configuration["MicrosoftAuth:TenantId"],
    builder.Configuration["AzureAd:TenantId"]);

externalAuth.EntraId.ClientId = FirstNonEmpty(
    externalAuth.EntraId.ClientId,
    builder.Configuration["MicrosoftAuth:ClientId"],
    builder.Configuration["AzureAd:ClientId"]);

static string FirstNonEmpty(params string?[] values)
    => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton(vectorOptions);
builder.Services.AddSingleton(externalAuth);
builder.Services.AddSingleton<IExternalTokenValidator, ExternalTokenValidator>();

// ---------------- database ----------------
// Azure SQL is the target described in the technical document; SQLite is the default so the
// application runs with no external dependency. Switch with Database:Provider=SqlServer.
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(connectionString ??
            throw new InvalidOperationException("ConnectionStrings:Default is required for SqlServer."));
    }
    else
    {
        var path = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "ragkb.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        options.UseSqlite(string.IsNullOrWhiteSpace(connectionString)
            ? $"Data Source={path}" : connectionString);
    }
});

// ---------------- auth ----------------
var tokenService = new TokenService(jwtOptions);
builder.Services.AddSingleton<ITokenService>(tokenService);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = tokenService.Key,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Roles are hierarchical, so each policy accepts its own level and everything above it.
    void AtLeast(string policy, UserRole minimum) =>
        options.AddPolicy(policy, p => p.RequireAssertion(ctx =>
        {
            var claim = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            return Enum.TryParse<UserRole>(claim, out var role) && role >= minimum;
        }));

    AtLeast(Policies.ChatbotAdmin, UserRole.ChatbotAdmin);
    AtLeast(Policies.KnowledgeAdmin, UserRole.KnowledgeAdmin);
    AtLeast(Policies.CompanyAdmin, UserRole.CompanyAdmin);
    AtLeast(Policies.SuperAdmin, UserRole.SuperAdmin);
});

// ---------------- application services ----------------
var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>()
                     ?? new StorageOptions();
builder.Services.AddSingleton(storageOptions);

// Azure Blob is the target in the technical document; the local filesystem is the default so the
// app runs with no cloud account. Both use the same {tenantId}/{knowledgeBaseId}/{file} key shape.
if (storageOptions.Provider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase)
    && storageOptions.Azure.Enabled)
    builder.Services.AddSingleton<IDocumentStorage, AzureBlobStorage>();
else
    builder.Services.AddSingleton<IDocumentStorage, LocalFileStorage>();

builder.Services.AddSingleton<StorageHealth>();
builder.Services.AddSingleton<ITenantSlugResolver, TenantSlugResolver>();
builder.Services.AddScoped<StorageMigrator>();
builder.Services.AddSingleton<TextExtractionService>();
builder.Services.AddSingleton<Chunker>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddScoped<RagService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<DocumentProcessor>();
builder.Services.AddScoped<WorkspaceProvisioner>();
builder.Services.AddScoped<DatabaseSeeder>();
builder.Services.AddHostedService<IngestionWorker>();

// Qdrant is the target in the technical document; the SQL store is the zero-dependency default.
if (vectorOptions.Provider.Equals("Qdrant", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>(
        c => c.Timeout = TimeSpan.FromSeconds(30));
else
    builder.Services.AddScoped<IVectorStore, SqlVectorStore>();

if (llmOptions.HasCredentials)
{
    builder.Services.AddHttpClient<IEmbeddingProvider, OpenAiEmbeddingProvider>(
        c => c.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddHttpClient<IChatCompletionProvider, OpenAiChatCompletionProvider>(
        c => c.Timeout = TimeSpan.FromSeconds(120));
}
else
{
    // No key configured: the app still runs end to end using the built-in providers.
    builder.Services.AddSingleton<IEmbeddingProvider, LocalEmbeddingProvider>();
    builder.Services.AddSingleton<IChatCompletionProvider, LocalChatCompletionProvider>();
}

// ---------------- web ----------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RAG Knowledge Base Enterprise RAG API",
        Version = "v1",
        Description = "Multi-tenant retrieval-augmented generation platform."
    });
    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    options.AddSecurityDefinition("Bearer", scheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };
builder.Services.AddCors(options => options.AddPolicy("ui", policy => policy
    .WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Unhandled failures return a small JSON body rather than an HTML error page the UI cannot parse.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    app.Logger.LogError(feature?.Error, "Unhandled exception on {Path}", context.Request.Path);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new
    {
        message = app.Environment.IsDevelopment()
            ? feature?.Error.Message ?? "Unexpected error."
            : "An unexpected error occurred."
    });
}));

app.UseSwagger();
app.UseSwaggerUI(o => o.DocumentTitle = "RAG Knowledge Base API");
app.UseCors("ui");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/swagger"));

using (var scope = app.Services.CreateScope())
{
    // Migrations rather than EnsureCreated, so a schema change is an incremental upgrade instead
    // of "drop the database and lose the data".
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var storage = scope.ServiceProvider.GetRequiredService<IDocumentStorage>();
    if (storage is AzureBlobStorage blobStorage)
    {
        try
        {
            await blobStorage.EnsureReadyAsync();

            // With the per-tenant strategy, give every existing tenant its container up front so
            // the account is legible in the portal before anyone uploads anything.
            var tenantIds = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync();
            await blobStorage.EnsureTenantContainersAsync(tenantIds);

            // Anything still only on local disk is copied up, keeping its existing key.
            await scope.ServiceProvider.GetRequiredService<StorageMigrator>().MigrateLocalFilesAsync();
        }
        catch (Exception ex)
        {
            var health = scope.ServiceProvider.GetRequiredService<StorageHealth>();
            health.Healthy = false;
            health.Error = ex.Message.Split('\n')[0].Trim();
            app.Logger.LogError(ex, "Azure Blob Storage is not reachable ({Provider})",
                storage.ProviderName);
        }
    }

    var vectors = scope.ServiceProvider.GetRequiredService<IVectorStore>();
    try
    {
        await vectors.EnsureReadyAsync();
    }
    catch (Exception ex)
    {
        // An unreachable index must not stop the API from starting: administration and chat
        // history still work, and ingestion will report the failure per document.
        app.Logger.LogError(ex, "Vector store {Provider} is not ready", vectors.ProviderName);
    }

    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
}

app.Run();
