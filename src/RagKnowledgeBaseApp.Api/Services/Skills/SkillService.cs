using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RagKnowledgeBaseApp.Api.Data;
using RagKnowledgeBaseApp.Api.Domain;
using RagKnowledgeBaseApp.Api.Services.Llm;

namespace RagKnowledgeBaseApp.Api.Services.Skills;

/// <summary>Makes a chatbot's skills selectable by the model and hands back their instructions.
///
/// Selection is the model's, not ours. Each installed skill is offered as a function whose
/// description is the skill's own, and adopting one is a call the model chooses to make. That is
/// what the description field is for, and it is why a vague description produces a skill that
/// either never fires or fires on everything.
///
/// Reusing the tool-calling loop rather than inventing a second mechanism means a skill adoption is
/// recorded, bounded by the same round limit, and visible in the same place as a tool call.</summary>
public class SkillService
{
    /// <summary>Prefix on the function name the model calls to adopt a skill. Kept distinct from
    /// tool names so the two can never collide in the function list.</summary>
    public const string FunctionPrefix = "skill_";

    private readonly AppDbContext _db;

    public SkillService(AppDbContext db) => _db = db;

    /// <summary>Installed, active skills attached to this chatbot, with the tools they carry.</summary>
    public async Task<List<Skill>> ForChatbotAsync(Guid chatbotId, Guid tenantId,
        CancellationToken ct = default)
    {
        var attached = _db.ChatbotSkills.Where(m => m.ChatbotId == chatbotId).Select(m => m.SkillId);

        return await _db.Skills.AsNoTracking()
            .Where(s => attached.Contains(s.Id) && s.TenantId == tenantId
                        && s.IsActive && s.IsInstalled)
            .Include(s => s.Tools).ThenInclude(t => t.Tool!).ThenInclude(t => t.Operations)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>The function list offered to the model, one per skill.
    ///
    /// No parameters: adopting a skill is a decision, not a query. The model calls it, receives the
    /// instructions, and continues with them in hand.</summary>
    public static List<ToolDefinition> Describe(IEnumerable<Skill> skills) =>
        skills.Select(s => new ToolDefinition(
            FunctionName(s),
            $"{s.Description} Call this before answering when the request is of this kind; "
            + "it returns instructions to follow.",
            """{"type":"object","properties":{}}"""))
        .ToList();

    public static string FunctionName(Skill skill) =>
        FunctionPrefix + Regex.Replace(skill.Name, "[^a-zA-Z0-9_-]", "_");

    public static Skill? Resolve(IEnumerable<Skill> skills, string functionName) =>
        skills.FirstOrDefault(s =>
            FunctionName(s).Equals(functionName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the model receives when it adopts a skill: the instructions, and a note of any
    /// tools the skill has just made available so it knows they are there.</summary>
    public static string Activate(Skill skill)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Skill: {skill.Name}");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(skill.Instructions)
            ? "This skill has no instructions recorded. Answer normally."
            : skill.Instructions);

        var tools = skill.Tools.Where(t => t.Tool is { IsActive: true }).ToList();
        if (tools.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("These tools are now available to you: "
                          + string.Join(", ", tools.Select(t => t.Tool!.Name)) + ".");
        }

        sb.AppendLine();
        sb.AppendLine("Follow the instructions above for the rest of this answer.");
        return sb.ToString();
    }

    /// <summary>Parses a SKILL.md file: YAML frontmatter carrying at least a name and description,
    /// followed by the markdown body.
    ///
    /// A deliberately small parser rather than a YAML dependency: the frontmatter of this format is
    /// a flat set of scalars, and accepting only that is the honest surface to support.</summary>
    public static (string? Name, string? Description, string? Tags, string? Version, string Body,
        string? Error) ParseSkillMarkdown(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, null, null, null, "", "The file is empty.");

        var text = content.Replace("\r\n", "\n").TrimStart('﻿', ' ', '\n');
        if (!text.StartsWith("---"))
            return (null, null, null, null, text,
                "SKILL.md must begin with YAML frontmatter delimited by ---.");

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return (null, null, null, null, text, "The YAML frontmatter is not closed with ---.");

        var frontmatter = text[3..end];
        var body = text[(end + 4)..].TrimStart('\n');

        string? name = null, description = null, tags = null, version = null;
        foreach (var raw in frontmatter.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "name": name = value; break;
                case "description": description = value; break;
                // Accepts either a comma list or a YAML flow sequence, which is how the format is
                // written in practice.
                case "tags": tags = value.Trim('[', ']').Replace("\"", "").Replace("'", ""); break;
                case "version": version = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return (null, null, null, null, body, "The frontmatter has no name.");
        if (string.IsNullOrWhiteSpace(description))
            return (name, null, tags, version, body, "The frontmatter has no description.");

        return (name, description, tags, version, body, null);
    }

    /// <summary>Renders a skill back out as SKILL.md, so what was imported can be exported.</summary>
    public static string ToSkillMarkdown(Skill skill)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {skill.Name}");
        sb.AppendLine($"description: {Escape(skill.Description)}");
        if (!string.IsNullOrWhiteSpace(skill.Tags))
            sb.AppendLine($"tags: [{skill.Tags}]");
        sb.AppendLine($"version: {skill.Version}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(skill.Instructions);
        return sb.ToString();

        // A colon in an unquoted scalar would break the frontmatter on the way back in.
        static string Escape(string value) =>
            value.Contains(':') || value.Contains('#') ? $"\"{value.Replace("\"", "'")}\"" : value;
    }
}
