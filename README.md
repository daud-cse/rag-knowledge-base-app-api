# Uttor AI — Enterprise RAG API

The **ASP.NET Core 8** back end of a multi-tenant, retrieval-augmented generation platform.

The Angular front end lives in its own repository: **[uttor-ai-ui](https://github.com/daud-cse/uttor-ai-ui)**.
Run both together — the UI proxies `/api` to this service on `http://localhost:5210`.

Storage, vector search, the database and the LLM all sit behind interfaces, so each one can be
pointed at a local or a cloud implementation through configuration alone.

## Current configuration

| Component | In use | Alternative |
|---|---|---|
| Database | **SQL Server Express** — local instance, database `UttorAI` | SQLite (`Database:Provider=Sqlite`) |
| Vector index | **Qdrant** — `http://localhost:6333`, collection `uttorai_chunks_1536` | SQL-backed (`VectorStore:Provider=Sql`) |
| Document storage | **Azure Blob** — account `uttorai`, one container per tenant | Local filesystem (`Storage:Provider=Local`) |
| LLM | **OpenAI** — `gpt-4o-mini` | Azure OpenAI, or the built-in local engine |
| Embeddings | **OpenAI** — `text-embedding-3-small`, 1536 dims | Azure OpenAI, or local hashed n-gram |
| Identity | Local password + **Google SSO** | Microsoft Entra ID (config only), SAML |

Secrets (the OpenAI key, the storage connection string) live in **.NET user secrets**, not in
`appsettings.json`, so they never enter source control. Public values (the Google client id, the
storage account name) stay in `appsettings.json` where they are easy to see:

```bash
cd src/UttorAI.Api
dotnet user-secrets list
dotnet user-secrets set "Llm:ApiKey" "sk-..."
```

The Qdrant collection name carries the vector width, so changing the embedding model creates a
separate collection instead of failing against an incompatible one. The collection is created on
startup if it does not exist.

---

## Quick start

Prerequisites: .NET 8 SDK, Node 20, the SQL Express instance running, and Qdrant on port 6333.

Two terminals, from this folder:

```bash
cd src/UttorAI.Api
dotnet run --urls http://localhost:5210
```

Swagger is at <http://localhost:5210/swagger>. Then start the UI from the
[uttor-ai-ui](https://github.com/daud-cse/uttor-ai-ui) repository and open
<http://localhost:4300>.

On first run the API creates the `UttorAI` database and the Qdrant collection, seeds two companies,
five accounts and a knowledge base with four sample documents, and indexes them.

To start over: drop the `UttorAI` database and delete the `uttorai_chunks_1536` collection; both are
recreated and reseeded on the next start.

### Demo accounts — password `Passw0rd!`

| Account | Role | Clearance | Can do |
|---|---|---|---|
| `super@uttor.ai` | Super Admin | Restricted | Everything, plus create/suspend companies |
| `admin@contoso.com` | Company Admin | Restricted | Users, roles, audit log, all of the below |
| `knowledge@contoso.com` | Knowledge Admin | Confidential | Company knowledge bases and documents |
| `user@contoso.com` | User | Internal | Chat, personal knowledge base, own conversations |
| `admin@northwind.com` | Company Admin | Restricted | A second company, to demonstrate isolation |

**Try this:** sign in as `user@contoso.com` and ask *"What is the executive lodging cap?"* — the
answer is in `Executive_Reimbursement_Policy.md`, which is classified `Restricted`. The user's
clearance is `Internal`, so the document is invisible to retrieval and the assistant correctly says
it cannot find it. Sign in as `admin@contoso.com` and the same question is answered with a citation.

---

## What is implemented

| # | Technical document section | Where |
|---|---|---|
| 1 | Chatbot configuration — prompt, model, temperature, tokens, RAG toggle, citations, language, welcome message, suggested questions, timeout, history | `Chatbot`, `ChatbotsController`, Admin → Chatbots |
| 2 | Knowledge base management — create, upload, delete, versioning, metadata, status | `KnowledgeBase`, `Document`, `DocumentsController` |
| 3 | Ingestion pipeline — validate → extract → chunk → embed → index, with live status and failure reasons | `DocumentProcessor`, `IngestionWorker` |
| 4 | Chatbot ↔ knowledge base mapping with priority | `ChatbotKnowledgeBase`, Admin → Chatbots → Knowledge bases |
| 5 | End-user upload — private to one conversation, never joins the company knowledge base | `ChatController.Attach`, chat composer 📎 |
| 6 | Multi-tenancy — company **and** personal workspaces as one `Tenant` concept, isolated at database, storage, index, API and retrieval level | `TenantType`, `WorkspaceProvisioner`, `CurrentUser.TenantId` from the JWT |
| 7 | Personal knowledge base — searched alongside company content, only for its owner | `KnowledgeBaseScope.Personal` |
| 8 | Enterprise authentication — **Google SSO working end to end**, Entra ID ready to enable, ID tokens verified against provider JWKS, company sign-up, individual sign-up, domain-to-company mapping | `ExternalIdentity.cs`, `AuthController` |
| 9 | RBAC — five-role ladder enforced by policy on every endpoint and mirrored in route guards | `Policies`, `roleGuard` |
| 10 | Document-level security (security trimming) — classification vs. user clearance, pushed into the search engine before retrieval | `QdrantVectorStore`, `SqlVectorStore` |
| 11 | Chat — new/continue/rename/delete/search conversations, regenerate, copy, feedback, follow-ups, export | `ChatController`, chat page |
| 12 | Citations and source tracking — numbered markers, file, page/section, knowledge base, score, snippet | `RagService.BuildContext` |
| 13 | RAG configuration — chunk size/overlap, embedding model, top-K, threshold, hybrid search, reranking, query rewriting | `KnowledgeBase`, `Chatbot`, `RagService` |
| 14 | Analytics — questions/day, success and no-answer rate, latency, tokens, cost, feedback, top chatbots and knowledge bases | `AnalyticsController`, Admin → Dashboard |
| 15 | Audit logging — auth, uploads, downloads, deletes, config changes, and every question with the sources retrieved | `AuditService`, Admin → Audit log |
| 16 | Data governance — classification, versioning with automatic archival, ephemeral uploads with expiry, and deletion that purges database, blob storage and vector index together | `Document`, `DocumentsController`, `TenantsController.Delete` |
| 17 | Admin portal | served by the [UI repository](https://github.com/daud-cse/uttor-ai-ui) |

---

## Architecture

```
Angular 18 (standalone, signals)
        │  /api  (dev-server proxy)
        ▼
ASP.NET Core 8  ── JWT auth · RBAC policies · tenant scoping · audit
        │
        ├── IDocumentStorage    → Azure Blob          (swap: local filesystem)
        ├── IVectorStore        → Qdrant              (swap: SQL + cosine)
        ├── IEmbeddingProvider  → OpenAI 1536-dim      (swap: Azure OpenAI / local)
        ├── IChatCompletion...  → OpenAI gpt-4o-mini   (swap: Azure OpenAI / local)
        └── EF Core             → SQL Server Express  (swap: Azure SQL / SQLite)
```

SQL Server holds all configuration, documents metadata, chunk text and conversations, and is the
system of record. Qdrant holds the vectors and is the index over it; the two are kept in step by
`DocumentProcessor` on ingest and by explicit cleanup on every delete path.

### Retrieval path

```
question → query rewriting → hybrid search (vector + keyword)
        → SECURITY TRIM (tenant, knowledge base, owner, classification, document status)
        → top-K → rerank + dedupe → fill the context budget → LLM → answer + citations
```

Context is bounded by **tokens**, not by a chunk count. `Chatbot.MaxContextTokens` (default 12,000)
is the real limit; `RerankTopN` is only a floor. Two consequences:

- **A corpus that fits the budget is sent whole, in document order.** A 40-page manual or a resume
  is ~7,000 tokens, so the model sees all of it rather than the five most similar fragments. This is
  what makes "how many suppliers are listed?" or "list every clause" answerable — questions that
  top-k similarity search cannot satisfy by construction, because they need every relevant passage
  rather than the closest few.
- **Aggregation questions widen the search.** "how many", "list all", "total", "compare" and similar
  triple the candidate pool before reranking.

The whole-corpus path applies exactly the same security predicates as the vector stores, so
"send everything" still means *everything this user is cleared to read* — a Restricted document
stays invisible to an Internal-clearance user.

The security trim is pushed down into the search engine — a SQL `WHERE` clause for the SQL store, a
Qdrant payload filter (`tenantId`, `knowledgeBaseId`, `classification`, `ownerUserId`) for Qdrant —
so a trimmed chunk never enters the process, let alone the prompt. Tenant comes from the JWT, never
from the request body.

### Layout

```
src/UttorAI.Api/
  Domain/       entities and enums
  Data/         DbContext, seeder, sample documents
  Auth/         JWT issuing, claims, CurrentUser, RBAC policies
  Services/
    Ingestion/  text extraction (PDF/Word/Excel/PowerPoint/text), chunking, pipeline worker
    Llm/        provider abstractions, OpenAI/Azure client, local fallbacks
    Vector/     vector store abstraction + SQL implementation with the security filter
    Storage/    document storage abstraction + local filesystem
    RagService  the retrieval pipeline
  Controllers/  auth, tenants, users, knowledge bases, documents, chatbots, chat, analytics, system
```

The front end is in [uttor-ai-ui](https://github.com/daud-cse/uttor-ai-ui).

---

## Running without an LLM key

With no key configured the API uses:

- **Embeddings** — a deterministic hashed n-gram vector (384 dimensions). Not competitive with a
  real embedding model, but stable and dependency-free.
- **Generation** — an *extractive* engine that selects the sentences from the retrieved passages
  that best match the question and returns them with citation markers. It never invents text; if
  nothing matches it says so.

The sidebar and login page show which providers are active, and the app displays a "Demo mode"
banner so this is never mistaken for a live model.

## Switching to real services

All of these are `appsettings.json` (or environment variable / user secret) changes — no code edits.

```jsonc
{
  "Llm": {
    "Provider": "OpenAI",              // or "AzureOpenAI"
    "ApiKey": "sk-...",
    "Endpoint": "https://api.openai.com/v1",
    "ChatModel": "gpt-4o-mini",
    "EmbeddingModel": "text-embedding-3-small",
    "EmbeddingDimensions": 1536
  },

  "Jwt": { "SigningKey": "<a long random secret>" },

  "Authentication": {
    "EntraId": { "TenantId": "<guid>", "ClientId": "<guid>" }
  }
}
```

Environment variable form, e.g. `Llm__ApiKey=sk-...`, `Llm__Provider=OpenAI`.

Notes:

- Switching to OpenAI embeddings moves you from 384 to 1536 dimensions, so retrieval starts using a
  new Qdrant collection (`uttorai_chunks_1536`) that is empty. **Re-index every document**
  (Admin → Knowledge base → document → *Re-index*) after switching, or nothing will be findable.
  The old collection is left alone so you can switch back.
- `Jwt:SigningKey` falls back to a per-machine development key. Set it explicitly anywhere real.
- The embedding provider no longer falls back to the local embedder when the API call fails: mixing
  vector spaces would silently corrupt retrieval, so the document fails with a visible reason.
- Azure Blob is implemented; see "Document storage" below for how to switch it on.

---

## Two kinds of workspace

Every workspace is a `Tenant` row; what differs is its `TenantType`.

```
                        Uttor AI
                            │
              ┌─────────────┴─────────────┐
        Company workspace          Personal workspace
              │                            │
     admin invites employees        one person, owns everything
     shared knowledge bases         private chatbots and documents
     roles and clearances           starter chatbot created for them
```

Because both are the same row shape, retrieval, storage, RBAC and auditing are identical for the
two — nothing downstream knows or cares which kind it is looking at.

### How someone gets a workspace

| Route | Outcome |
|---|---|
| Sign-up page → **Just me** | A personal workspace named after them, with a starter chatbot and knowledge base |
| Sign-up page → **My company** | A company workspace, and they become its Company Admin |
| Google/Microsoft, email domain matches a company | They join that company as a normal **User** at **Internal** clearance |
| Google/Microsoft, no match | A personal workspace is created automatically |
| Company admin adds them in Users & roles | An account they activate by signing in with SSO (leave the password blank) or with a password you set |

A personal workspace never claims an email domain, so an individual can never be pulled into
somebody else's private space. Both routes can be turned off with
`Authentication:AllowIndividualSignup` and `Authentication:AllowCompanySignup`.

The owner of a personal workspace holds `CompanyAdmin` **inside their own tenant only** — it lets
them create chatbots and knowledge bases there and grants nothing anywhere else, because every query
is scoped by tenant first.

### Deleting a workspace

Removing a tenant purges its database rows, its blobs (the whole container under the per-tenant
strategy) and its vectors from Qdrant. Offboarding a customer leaves nothing behind in any of the
three stores.

## Single sign-on

Both providers use the same flow. The browser obtains an **ID token** from the identity provider;
the API verifies that token's signature against the provider's published JWKS, along with its
issuer, audience and expiry; only then does it issue an application session.

```
browser ──▶ Google / Entra ID ──▶ ID token
                                     │
                                     ▼
                    POST /api/auth/external { provider, idToken }
                                     │
                       verify signature · issuer · audience · expiry
                                     │
                     resolve account ──▶ application JWT (tenant, role, clearance)
```

The external token establishes **who** someone is. Which company they belong to, what role they
hold and which classifications they may retrieve remain entirely this application's decision.

### How an SSO user is matched to a company

1. If a user with that email already exists, they sign in as that account — an account created by
   an admin with a password can also sign in with Google, and the identities are linked on first use.
2. Otherwise the email domain is matched against each company's **allowed email domains**
   (Admin → Companies → Edit). A match provisions a new account as a plain **User** with
   **Internal** clearance.
3. No match means sign-in is refused with a message naming the domain to add.

Auto-provisioning can be turned off entirely with `Authentication:AutoProvision=false`, which makes
step 2 fail and requires an administrator to create every account.

### Configuring Google

A Google client id is public (the browser hands it to Google), so it lives in `appsettings.json`:

```jsonc
"GoogleAuth": { "ClientId": "<id>.apps.googleusercontent.com" }
```

`Authentication:Google:ClientId` is accepted as an equivalent; whichever is set wins, with the
nested form taking priority.

In the Google Cloud console, the OAuth client needs the app's origin under
**Authorized JavaScript origins** — `http://localhost:4300` for local development. No client secret
is used or needed: this is the ID-token flow for a browser app.

### Configuring Microsoft Entra ID

Register a **single-page application** in Entra ID with redirect URI `http://localhost:4300`, then
fill in `appsettings.json` (these ids are public too):

```jsonc
"MicrosoftAuth": {
  "TenantId": "<directory (tenant) id>",
  "ClientId": "<application (client) id>"
}
```

`Authentication:EntraId:*` and `AzureAd:*` are accepted as equivalents.

The button appears on the login page as soon as both values are present; nothing else changes.
Use `common` as the tenant id to accept any Microsoft work or school account.

## Document storage

Storage keys always have the shape `{tenantId}/{knowledgeBaseId}/{unique}_{fileName}`. On disk that
is a directory tree; in Azure Blob Storage the identical string is the blob name, and the slashes
render as **folders per tenant** in Storage Explorer and the portal. One key shape means switching
provider never invalidates a stored row.

### Container strategy

`PerTenant` (the default) gives each company **its own container, named after the tenant slug**, so
the storage account reads clearly in the portal:

```
uttorai-documents-contoso/                ← Contoso Health
  0ad876a8-…/…_Claims_Guideline.md
  0ad876a8-…/…_Provider_Manual.md
uttorai-documents-northwind/              ← Northwind Insurance
  …
```

`Single` puts every company in one container, one folder per tenant id:

```
uttorai-documents/
  a5908720-…-28dc8331b4bb/0ad876a8-…/…_Claims_Guideline.md
  7f2c1a90-…-11ab34cd56ef/…
```

| Strategy | Container | Blob name | Good for |
|---|---|---|---|
| `PerTenant` (default) | `{prefix}-{tenantSlug}` | `{kbId}/{unique}_{file}` | Readable in the portal; SAS tokens, RBAC, lifecycle rules and deletion can all be scoped per customer |
| `Single` | `{prefix}` | `{tenantId}/{kbId}/{unique}_{file}` | One container to manage |

Both are driven by the same stored key, so the strategy can change without rewriting a single row —
only where the bytes live moves. Container names come from the tenant **slug**, which this API fixes
at creation and never lets you change, so a container name stays valid for the tenant's lifetime. If
a slug cannot be resolved the name falls back to the tenant id, so a blob is never unreachable.

You do **not** create containers by hand — the app creates one per tenant at startup and on demand.

### Configuration

The account name is not a secret, so it sits in `appsettings.json`. The credential does not:

```jsonc
"Storage": {
  "Provider": "AzureBlob",
  "Azure": {
    "AccountName": "uttorai",
    "ContainerStrategy": "PerTenant", // or "Single"
    "Container": "uttorai-documents"
  }
}
```

Then supply a credential, either:

```bash
# Option A — connection string (works anywhere)
dotnet user-secrets set "Storage:Azure:ConnectionString" "DefaultEndpointsProtocol=https;AccountName=uttorai;AccountKey=...;EndpointSuffix=core.windows.net"

# Option B — passwordless, preferred in Azure. No secret at all: grant the signed-in user or the
# app's managed identity the "Storage Blob Data Contributor" role on the account, then leave
# ConnectionString empty and DefaultAzureCredential is used.
```

The container is created on startup if missing. On the first start after switching, any document
still only on local disk is **copied up to blob under its existing key** — no rows change, and the
local copy is left in place as a fallback.

If the account is unreachable at startup the API still runs, and `/api/system/status` reports the
failure so the UI shows a banner instead of the problem only appearing at the first upload.

## Supported document types

`.pdf` (PdfPig, per-page citations) · `.docx` (per-heading sections) · `.xlsx` (per sheet) ·
`.pptx` (per slide) · `.txt` `.md` `.csv` `.json` `.html` `.htm` `.log` `.xml`

Scanned PDFs with no selectable text fail with an explicit "needs OCR" message rather than
indexing an empty document. Upload limit is 50 MB per file (25 MB for chat attachments).

## API

Swagger UI is at <http://localhost:5210/swagger>. Click **Authorize** and paste the
`accessToken` from `POST /api/auth/login`.

## Verification

The API builds clean with no warnings. An end-to-end test covering ingestion,
retrieval, citations, attachments, security trimming, tenant isolation, RBAC, analytics and audit
passes 27/27 against the running stack on SQL Server + Qdrant + OpenAI, and a workspace suite
covers company sign-up, individual sign-up, starter-workspace creation, employee invitation and
isolation between a personal workspace and a company (22/22). The Qdrant collection and
the `DocumentChunks` table hold exactly the same set of ids (no orphans, nothing unindexed). A
separate SSO suite confirms the API rejects forged, wrongly-issued, wrongly-audienced, expired and
malformed identity tokens — including a forged token claiming to be `admin@contoso.com`.

## Not built

Streaming (SSE) responses, OCR for scanned PDFs, SharePoint/OneDrive/Blob connectors, PII
detection, retention/expiry jobs, and SAML (Google and Entra ID are done; SAML is advertised but
not implemented). Automated unit tests are not included — verification is the end-to-end suites
described above.
