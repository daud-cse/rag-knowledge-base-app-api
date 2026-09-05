namespace RagKnowledgeBaseApp.Api.Services.Tools;

/// <summary>One third-party application a connector tool can be pointed at.</summary>
/// <param name="Key">Stable identifier stored on the tool, e.g. GITHUB. Never localised.</param>
/// <param name="Name">Shown to an administrator picking an app.</param>
/// <param name="Category">Groups the picker so a long list stays scannable.</param>
/// <param name="Description">One line explaining what connecting the app would let an assistant do.</param>
/// <param name="Colour">Brand-ish colour for the monogram tile. Real logos are deliberately not
/// shipped: they are third-party trademarks, and a coloured monogram reads just as clearly.</param>
public record ConnectorApp(string Key, string Name, string Category, string Description, string Colour);

/// <summary>The applications a connector tool may target.
///
/// Held on the server rather than in the front end so the list is the same for every client, can be
/// validated on write, and can later be replaced by whatever a connector provider actually offers
/// without shipping a new UI.</summary>
public static class ConnectorCatalog
{
    public static readonly IReadOnlyList<ConnectorApp> Apps = new List<ConnectorApp>
    {
        // ---- developer tools ----
        new("GITHUB", "GitHub", "Developer tools",
            "Repositories, issues, pull requests and code search.", "#24292f"),
        new("GITLAB", "GitLab", "Developer tools",
            "Projects, issues and merge requests.", "#fc6d26"),
        new("BITBUCKET", "Bitbucket", "Developer tools",
            "Repositories and pull requests.", "#0052cc"),
        new("JIRA", "Jira", "Developer tools",
            "Issues, sprints and project boards.", "#0052cc"),

        // ---- storage ----
        new("ONE_DRIVE", "OneDrive", "File storage",
            "Files and folders in a Microsoft 365 account.", "#0364b8"),
        new("SHARE_POINT", "SharePoint", "File storage",
            "Document libraries and team sites.", "#038387"),
        new("GOOGLE_DRIVE", "Google Drive", "File storage",
            "Documents, spreadsheets and folders.", "#1a73e8"),
        new("DROPBOX", "Dropbox", "File storage",
            "Shared files and folders.", "#0061ff"),
        new("BOX", "Box", "File storage",
            "Enterprise content and shared folders.", "#0061d5"),

        // ---- communication ----
        new("SLACK", "Slack", "Communication",
            "Channels, messages and search across a workspace.", "#4a154b"),
        new("MICROSOFT_TEAMS", "Microsoft Teams", "Communication",
            "Teams, channels and chat messages.", "#5059c9"),
        new("GMAIL", "Gmail", "Communication",
            "Read, search and send mail.", "#ea4335"),
        new("OUTLOOK", "Outlook", "Communication",
            "Mail and contacts in a Microsoft 365 account.", "#0078d4"),
        new("DISCORD", "Discord", "Communication",
            "Servers, channels and messages.", "#5865f2"),

        // ---- documentation ----
        new("NOTION", "Notion", "Documentation",
            "Pages, databases and workspace search.", "#111111"),
        new("CONFLUENCE", "Confluence", "Documentation",
            "Spaces and pages in an Atlassian site.", "#172b4d"),

        // ---- productivity ----
        new("GOOGLE_CALENDAR", "Google Calendar", "Productivity",
            "Events, availability and scheduling.", "#1a73e8"),
        new("GOOGLE_SHEETS", "Google Sheets", "Productivity",
            "Read and write spreadsheet data.", "#0f9d58"),
        new("ASANA", "Asana", "Productivity",
            "Tasks, projects and assignments.", "#f06a6a"),
        new("TRELLO", "Trello", "Productivity",
            "Boards, lists and cards.", "#0079bf"),
        new("LINEAR", "Linear", "Productivity",
            "Issues, cycles and project tracking.", "#5e6ad2"),

        // ---- business systems ----
        new("SALESFORCE", "Salesforce", "Business systems",
            "Accounts, contacts, leads and opportunities.", "#00a1e0"),
        new("HUBSPOT", "HubSpot", "Business systems",
            "Contacts, deals and marketing records.", "#ff7a59"),
        new("ZENDESK", "Zendesk", "Business systems",
            "Support tickets and customer history.", "#03363d"),
        new("STRIPE", "Stripe", "Business systems",
            "Customers, payments and subscriptions.", "#635bff")
    };

    public static ConnectorApp? Find(string? key) => string.IsNullOrWhiteSpace(key)
        ? null
        : Apps.FirstOrDefault(a => a.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));
}
