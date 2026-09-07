using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Auth;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Dtos;
using RagKnowledgeBaseApp.Api.Services;
using RagKnowledgeBaseApp.Api.Services.Skills;

namespace RagKnowledgeBaseApp.Api.Controllers;

/// <summary>The skill catalogue: writing skills, importing them from the portable SKILL.md format,
/// and exporting them back out.</summary>
[ApiController]
[Route("api/skills")]
[Authorize(Policy = Policies.ChatbotAdmin)]
public class SkillsController : ControllerBase
{
    /// <summary>An imported archive is untrusted input; a skill is text, so this is generous.</summary>
    private const int MaxUploadBytes = 5 * 1024 * 1024;
    private const int MaxInstructions = 50_000;

    private readonly AppDbContext _db;
    private readonly CurrentUser _current;
    private readonly AuditService _audit;

    public SkillsController(AppDbContext db, CurrentUser current, AuditService audit)
    {
        _db = db;
        _current = current;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<SkillDto[]>> List([FromQuery] bool onlyInstalled,
        [FromQuery] string? search, CancellationToken ct)
    {
        var query = _db.Skills.AsNoTracking().Where(s => s.TenantId == _current.TenantId);
        if (onlyInstalled) query = query.Where(s => s.IsInstalled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => EF.Functions.Like(s.Name, $"%{term}%")
                                     || EF.Functions.Like(s.Description, $"%{term}%")
                                     || (s.Tags != null && EF.Functions.Like(s.Tags, $"%{term}%")));
        }

        var skills = await query
            .Include(s => s.Tools).ThenInclude(t => t.Tool!).ThenInclude(t => t.Operations)
            .OrderBy(s => s.Name).AsSplitQuery().ToListAsync(ct);

        return Ok(skills.Select(Map).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SkillDto>> Get(Guid id, CancellationToken ct)
    {
        var skill = await Find(id, ct);
        return skill is null ? NotFound(new { message = "Skill not found." }) : Ok(Map(skill));
    }

    /// <summary>Exports a skill as SKILL.md, so a skill written here is portable.</summary>
    [HttpGet("{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        var skill = await Find(id, ct);
        if (skill is null) return NotFound(new { message = "Skill not found." });

        var markdown = SkillService.ToSkillMarkdown(skill);
        return File(Encoding.UTF8.GetBytes(markdown), "text/markdown", $"{skill.Name}-SKILL.md");
    }

    [HttpPost]
    public async Task<ActionResult<SkillDto>> Create(SkillSaveRequest request, CancellationToken ct)
    {
        var name = Slug(request.Name);
        if (name.Length == 0) return BadRequest(new { message = "A skill needs a name." });
        if (string.IsNullOrWhiteSpace(request.Description))
            return BadRequest(new
            {
                message = "A description is required — the model reads it to decide whether to use the skill."
            });
        if ((request.Instructions?.Length ?? 0) > MaxInstructions)
            return BadRequest(new { message = $"Instructions are limited to {MaxInstructions:N0} characters." });

        if (await _db.Skills.AnyAsync(s => s.TenantId == _current.TenantId && s.Name == name, ct))
            return BadRequest(new { message = $"A skill called '{name}' already exists." });

        var skill = new Skill
        {
            TenantId = _current.TenantId,
            Name = name,
            Description = request.Description.Trim(),
            Tags = CleanTags(request.Tags),
            Instructions = request.Instructions ?? "",
            Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim(),
            IsInstalled = request.IsInstalled,
            IsActive = request.IsActive,
            CreatedByUserId = _current.Id
        };
        _db.Skills.Add(skill);
        await _db.SaveChangesAsync(ct);

        await ReplaceToolsAsync(skill.Id, request.ToolIds, ct);
        await _audit.LogAsync("skill.create", "Skill", skill.Id.ToString(), new { skill.Name }, ct);

        return Ok(Map((await Find(skill.Id, ct))!));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<SkillDto>> Update(Guid id, SkillSaveRequest request,
        CancellationToken ct)
    {
        var skill = await Find(id, ct);
        if (skill is null) return NotFound(new { message = "Skill not found." });
        if ((request.Instructions?.Length ?? 0) > MaxInstructions)
            return BadRequest(new { message = $"Instructions are limited to {MaxInstructions:N0} characters." });

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = Slug(request.Name);
            if (!name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase) &&
                await _db.Skills.AnyAsync(s => s.TenantId == _current.TenantId && s.Name == name, ct))
                return BadRequest(new { message = $"A skill called '{name}' already exists." });
            skill.Name = name;
        }

        if (!string.IsNullOrWhiteSpace(request.Description)) skill.Description = request.Description.Trim();
        if (request.Tags is not null) skill.Tags = CleanTags(request.Tags);
        if (request.Instructions is not null) skill.Instructions = request.Instructions;
        if (!string.IsNullOrWhiteSpace(request.Version)) skill.Version = request.Version.Trim();
        skill.IsInstalled = request.IsInstalled;
        skill.IsActive = request.IsActive;
        skill.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        if (request.ToolIds is not null) await ReplaceToolsAsync(id, request.ToolIds, ct);

        await _audit.LogAsync("skill.update", "Skill", id.ToString(), new { skill.Name }, ct);
        _db.ChangeTracker.Clear();
        return Ok(Map((await Find(id, ct))!));
    }

    /// <summary>Installing is what makes a skill selectable when configuring a chatbot. Uninstalling
    /// leaves the skill in the catalogue and detaches it from every assistant, so nothing keeps
    /// using it silently.</summary>
    [HttpPost("{id:guid}/install")]
    public async Task<ActionResult<SkillDto>> SetInstalled(Guid id, [FromQuery] bool installed,
        CancellationToken ct)
    {
        var skill = await Find(id, ct);
        if (skill is null) return NotFound(new { message = "Skill not found." });

        skill.IsInstalled = installed;
        skill.UpdatedAt = DateTime.UtcNow;
        if (!installed)
            await _db.ChatbotSkills.Where(m => m.SkillId == id).ExecuteDeleteAsync(ct);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(installed ? "skill.install" : "skill.uninstall", "Skill",
            id.ToString(), new { skill.Name }, ct);

        _db.ChangeTracker.Clear();
        return Ok(Map((await Find(id, ct))!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var skill = await Find(id, ct);
        if (skill is null) return NotFound(new { message = "Skill not found." });

        _db.Skills.Remove(skill);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("skill.delete", "Skill", id.ToString(), new { skill.Name }, ct);
        return NoContent();
    }

    /// <summary>Imports a SKILL.md, or a .zip holding one or more of them.
    ///
    /// The archive is walked rather than extracted to disk: nothing here needs a file on the server,
    /// and reading entries in memory sidesteps path traversal entirely.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult<SkillImportResultDto>> Import(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "No file was uploaded." });
        if (file.Length > MaxUploadBytes)
            return BadRequest(new { message = "That file is larger than 5 MB." });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var documents = new List<(string Source, string Content)>();

        if (extension is ".md" or ".markdown" or ".txt")
        {
            using var reader = new StreamReader(file.OpenReadStream());
            documents.Add((file.FileName, await reader.ReadToEndAsync(ct)));
        }
        else if (extension == ".zip")
        {
            using var archive = new ZipArchive(file.OpenReadStream(), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                // Only SKILL.md files are read; anything else in the archive is ignored rather
                // than rejected, which is what the format's own tooling does.
                if (!entry.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length > MaxUploadBytes) continue;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                documents.Add((entry.FullName, await reader.ReadToEndAsync(ct)));
            }
            if (documents.Count == 0)
                return BadRequest(new { message = "That archive contains no SKILL.md file." });
        }
        else
        {
            return BadRequest(new { message = "Upload a SKILL.md file or a .zip skill archive." });
        }

        var imported = new List<string>();
        var warnings = new List<string>();
        var skipped = 0;

        foreach (var (source, content) in documents)
        {
            var (name, description, tags, version, body, error) = SkillService.ParseSkillMarkdown(content);
            if (error is not null)
            {
                skipped++;
                warnings.Add($"{source}: {error}");
                continue;
            }

            var slug = Slug(name!);
            if (await _db.Skills.AnyAsync(s => s.TenantId == _current.TenantId && s.Name == slug, ct))
            {
                skipped++;
                warnings.Add($"'{slug}' already exists and was left alone.");
                continue;
            }

            _db.Skills.Add(new Skill
            {
                TenantId = _current.TenantId,
                Name = slug,
                Description = Cap(description!, 1024),
                Tags = CleanTags(tags),
                Instructions = Cap(body, MaxInstructions),
                Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : Cap(version.Trim(), 20),
                IsInstalled = true,
                IsActive = true,
                CreatedByUserId = _current.Id
            });
            imported.Add(slug);
        }

        if (imported.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync("skill.import", "Skill", null,
                new { Count = imported.Count, Names = imported }, ct);
        }

        return Ok(new SkillImportResultDto(imported.Count, skipped, imported.ToArray(),
            warnings.ToArray()));
    }

    // ------------------------------ helpers ------------------------------

    private Task<Skill?> Find(Guid id, CancellationToken ct) =>
        _db.Skills.Include(s => s.Tools).ThenInclude(t => t.Tool!).ThenInclude(t => t.Operations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == _current.TenantId, ct);

    private async Task ReplaceToolsAsync(Guid skillId, Guid[]? toolIds, CancellationToken ct)
    {
        await _db.SkillTools.Where(t => t.SkillId == skillId).ExecuteDeleteAsync(ct);
        if (toolIds is null || toolIds.Length == 0) return;

        var valid = await _db.Tools.AsNoTracking()
            .Where(t => toolIds.Contains(t.Id) && t.TenantId == _current.TenantId)
            .Select(t => t.Id).ToListAsync(ct);

        foreach (var toolId in valid)
            _db.SkillTools.Add(new SkillTool { SkillId = skillId, ToolId = toolId });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Skill names are handles the model is given, so they are normalised to the same
    /// character set a function name allows rather than rejected for containing a space.</summary>
    private static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length <= 64 ? slug : slug[..64].Trim('-');
    }

    private static string? CleanTags(string? tags) => string.IsNullOrWhiteSpace(tags)
        ? null
        : Cap(string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct()), 400);

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    private static SkillDto Map(Skill s) => new(
        s.Id, s.Name, s.Description, s.Tags, s.Instructions, s.Version, s.IsInstalled, s.IsActive,
        s.CreatedAt, s.UpdatedAt,
        s.Tools.Where(t => t.Tool is not null).Select(t => new ToolLinkDto(
            t.ToolId, t.Tool!.Name, t.Tool.Type.ToString(), t.Tool.Operations.Count)).ToArray());
}
