# nl-bounded-research-agent

A .NET 10 research agent that chooses its next search, source read, evidence
record, or completion request through Microsoft Foundry function calling.
PostgreSQL stores the investigation, source snapshots, evidence ledger, tool
history, and transactional checkpoints.

## Overview

The sample investigates an engineering question: **Can HybridCache replace
Redis in a multi-instance .NET application?** The brief separates cache-layer
responsibilities from cross-instance invalidation. The model decides what to
investigate next. The host controls permitted tools, sources, evidence admission,
execution budgets, persistence, and final citation integrity.

The default brief has **no seed URLs**: the agent must discover pages through
Serper before reading them. It chooses subsequent queries and sources from the
remaining evidence gaps. Custom briefs may supply seeds; sufficient supplied
evidence can legitimately support completion without a search.

Primary newsletter area: **AI Workflows & Pipelines**. Companion to Issue #50,
"Building Research Agents with Search and Durable Evidence."

## What This Project Demonstrates

- Native Foundry function calls with one model-selected tool per turn
- Iterative investigation with `SearchWeb`, `ReadPage`, `RecordEvidence`, and `FinishResearch`
- Serper Search API for live source discovery
- Exact-host HTTPS source admission and bounded text extraction
- Exact-quote validation against stored source snapshots
- Explicit supported and contradictory evidence records
- PostgreSQL relational tables, JSONB run state, foreign keys, and migrations
- Atomic checkpoints, optimistic versions, and per-run session advisory locks
- Resume after process interruption and explicit captured-decision replay
- Incomplete outcomes when evidence or execution budgets run out
- Unit tests and opt-in tests against an actual PostgreSQL database

The runtime uses an explicit C# tool loop and Foundry's OpenAI-compatible REST
API so every model/tool boundary has an application-owned checkpoint. The live
path requires Foundry and Serper credentials. Test doubles stay in the test project;
`replay` is an explicit command over previously persisted decisions.

The search adapter sends `POST https://google.serper.dev/search` with an
`X-API-KEY` header and a JSON body containing `q` and `num: 10`. It maps Serper's
`organic` entries (`link`, `title`, `snippet`) into the internal search contract.
Allowed-host filtering, URL deduplication, and the configured result cap happen
before results reach the agent. Foundry supplies the model; Serper supplies search.
`ReadPage` fetches selected admitted URLs through separate HTTP requests. It can
read newly discovered pages, not just seeds; it does not automatically crawl page links.

## Prerequisites

- .NET 10 SDK
- PostgreSQL 17 or 18; Docker Compose is one local setup option
- A Microsoft Foundry deployment supporting chat-completions function calling,
  `tool_choice: required`, and `parallel_tool_calls: false`
- The deployment's **OpenAI-compatible inference base URL**, ending in `/openai/v1/`
- Foundry API key and Serper Search API key for live investigation

## Quick Start

Run commands from this repository directory.

### 1. Start a small PostgreSQL database

```powershell
Copy-Item .env.example .env
# Edit POSTGRES_PASSWORD in .env before starting the service.
docker compose up -d --wait
```

Compose uses `postgres:18-alpine`, a persistent named volume, a health check, and
loopback port `55432`. PostgreSQL 18's volume mount is `/var/lib/postgresql`.
An existing PostgreSQL instance works equally well; create a dedicated database
and use its connection string. No vector extension is required: this database
stores durable investigation records rather than embedding vectors.

### 2. Configure the application

```powershell
$env:RESEARCH_Database__ConnectionString = 'Host=localhost;Port=55432;Database=research;Username=research;Password=YOUR-LOCAL-PASSWORD'
$env:RESEARCH_Foundry__Endpoint = 'https://YOUR-RESOURCE.openai.azure.com/openai/v1/'
$env:RESEARCH_Foundry__Deployment = 'YOUR-TOOL-CALLING-DEPLOYMENT'
$env:RESEARCH_FOUNDRY_API_KEY = 'YOUR-FOUNDRY-KEY'
$env:RESEARCH_SERPER_API_KEY = 'YOUR-SERPER-KEY'
```

The application reads shell environment variables. `.env` is used by Compose;
it is not automatically loaded by .NET. Use the actual deployment name shown in
Foundry. Copy the inference URL from the deployment; a `/api/projects/...` URL
is a different API boundary and is rejected rather than guessed.

Use your existing Serper account and API key. If your shell already has
`SERPER_API_KEY`, reuse it with
`$env:RESEARCH_SERPER_API_KEY = $env:SERPER_API_KEY`, or set
`RESEARCH_Search__ApiKeyEnvironmentVariable` to `SERPER_API_KEY`.

### 3. Initialize and investigate

```powershell
dotnet restore BoundedResearchAgent.slnx
dotnet run --project BoundedResearchAgent -- init-db
dotnet run --project BoundedResearchAgent -- run
# Supply another brief explicitly:
dotnet run --project BoundedResearchAgent -- run data/briefs/hybrid-cache.json
```

`init-db` applies checked-in SQL migrations transactionally. Applied migration
hashes are recorded; modifying an applied migration causes an error. Add a new
numbered SQL migration to evolve the schema. The database role needs DDL
privileges for initialization and DML privileges for runtime use.

If the four initial tables already exist without a migration-history entry,
`init-db` compares their columns, constraints, and indexes with the checked-in
initial migration. Matching tables are adopted and their history is recorded;
existing runs are preserved. Partial or incompatible schemas are rejected and
the initialization transaction is rolled back.

The command prints a run UUID before model execution. The final JSON export goes
to `data/output/<run-id>.json`; PostgreSQL remains the source of truth.

### 4. Inspect, resume, and replay

```powershell
$researchRunId = 'REPLACE-WITH-RUN-UUID'
dotnet run --project BoundedResearchAgent -- report $researchRunId
dotnet run --project BoundedResearchAgent -- resume $researchRunId
dotnet run --project BoundedResearchAgent -- replay $researchRunId
```

Set `$researchRunId` to the printed UUID. `report` needs only database access.
`resume` continues a `running` run after an abrupt process interruption, keeping
the original deployment, limits, counters, transcript, and absolute deadline.
An expired deadline produces `incomplete`. Terminal runs are not reopened;
start a new run after a provider failure or deliberate cancellation.

For `reported_token_budget`, raise `RESEARCH_Limits__MaxReportedTokens` and
start a **new** run. Changing environment variables does not change the limits
stored in an existing run. The default is 100000 cumulative input-plus-output
tokens: each model turn includes prior source text again, so it is not merely
a limit on the final answer's length.

`replay` requires a captured run that reached `FinishResearch`. It re-executes
host decisions with the stored model tool calls and captured web results,
without calling Foundry or Serper. It stores a separate run with `mode=replay`
and `replayOf`, checks source-text hashes, and compares the final report/status.
It tests reproducibility of host decisions, not whether a model would independently
choose the same investigation again. Original model token counts are replayed
as historical accounting; replay consumes no model tokens.

Exit codes: `0` completed, `2` incomplete/failed research outcome, `1` command,
configuration, database, or replay-divergence error. Ctrl+C requests a checkpointed
incomplete stop. An abrupt kill leaves the last committed checkpoint available.

## Configuration

Defaults live in `BoundedResearchAgent/appsettings.json`. Override nested values
with the `RESEARCH_` prefix and `__` separators.

| Setting | Default / purpose |
| --- | --- |
| `Database:ConnectionString` | Dedicated local database on port 55432; no checked-in password |
| `Foundry:Endpoint` | Required inference base URL |
| `Foundry:Deployment` | Required tool-capable deployment |
| `Foundry:ApiKeyEnvironmentVariable` | `RESEARCH_FOUNDRY_API_KEY` |
| `Foundry:MaxOutputTokens` | 1800 per model response |
| `Search:ApiKeyEnvironmentVariable` | `RESEARCH_SERPER_API_KEY` |
| `Search:ResultsPerQuery` | 5 admitted results, capped locally from Serper's ten-result page |
| `Limits:MaxSteps` | 20, including rejected tool attempts |
| `Limits:MaxSearches` / `MaxPages` | 4 search attempts / 6 page-fetch attempts |
| `Limits:MaxEvidence` | 16 evidence entries |
| `Limits:MaxRunSeconds` | 300 seconds, retained across resume |
| `Limits:MaxReportedTokens` | 100000 provider-reported cumulative input-plus-output tokens |
| `Limits:MaxContextCharacters` | 100000 serialized request-message characters before a model call |
| `Limits:MaxPageCharacters` | 5000 normalized characters per source window returned to the model |
| `Limits:MaxSourceCharacters` | 100000 normalized characters per stored source snapshot |
| `Limits:MaxFinishAttempts` | 3 valid final-report submissions with unresolved gaps |
| `Limits:MaxResponseBytes` | 2000000 bytes per page response |
| `Limits:MinSources` | 2 distinct cited source URLs |

The token threshold is accounting based on returned usage. A single model call
can cross it; it is not a hard prepaid token/currency cap. Character bounds are
not tokenizer estimates. Requests also have individual HTTP timeouts. The host
checks the context bound before each call. The complete audit transcript remains
stored, while all but the two most recent page windows are replaced in model
requests by source references and offsets. Evidence quotes remain in context;
the agent can re-read a cached window without another network fetch.

## How It Works

1. Validate the brief and snapshot execution limits into a new database run.
2. Send the brief and persisted conversation to the configured Foundry deployment.
3. Require exactly one native function call; reject truncated or malformed replies.
4. Authorize and execute the selected tool. Tool errors return to the model as
   bounded data so it can choose another query or correct its arguments.
5. Save the resulting run state, new sources, evidence, and step rows in one
   PostgreSQL transaction. The checkpoint version advances only after commit.
6. Repeat until a completion request passes the report contract or a host limit stops work.

Completion requires every finding to cite recorded evidence. All required
question IDs must have evidence cited by the final findings, and the minimum
distinct cited URL count must be met. Only unresolved required research belongs
in `openQuestions`. Optional deployment choices and caveats belong in
`limitations` and do not block completion. A premature `FinishResearch` returns
the gaps and unread URLs so the agent can investigate and submit again. After
three unsuccessful submissions, or an execution limit, genuine gaps remain
`incomplete`; the host never clears them just to produce a successful status.

`ReadPage` stores the full bounded snapshot and returns a smaller window with
heading offsets and `nextOffset`. Pass `find` with a section heading/phrase or
`offset` with `nextOffset` to read later text from the same source. A short
window is distinct from `snapshotTruncated`, which means the stored source
itself reached its hard bound. Windows contain stable passage IDs; pass
`excerptId` to `RecordEvidence` and the host copies that exact source quotation.
Free-form quotes remain supported and must match verbatim. Search snippets cannot
be recorded as evidence without reading and quoting the corresponding source.

New runs persist `contractVersion=3`, which removes repeated evidence IDs within
each final finding while retaining the original submitted tool arguments in the
audit trail. Version 2 retains its original citation output during replay;
historical checkpoints without a version retain the original completion policy
and source-response format as version 1.

## Project Structure

```text
BoundedResearchAgent.slnx
compose.yaml
database/001_initial.sql
data/briefs/hybrid-cache.json
data/evaluation/research-cases.json
BoundedResearchAgent/
  Program.cs
  appsettings.json
  Agent/          # loop, tool schemas, explicit captured replay
  App/            # configuration and validation
  Domain/         # typed runs, snapshots, evidence, reports
  Foundry/        # native tool-calling REST adapter
  Persistence/    # migrations, locks, atomic checkpoints
  Web/            # Serper discovery, source policy, bounded extraction
BoundedResearchAgent.Tests/
docs/
  DATABASE.md
  EVALUATION.md
  VALIDATION.md
```

## Control Guarantees

- Reads use HTTPS on exact configured hosts; discovered/seed URLs are required.
- Redirect targets are revalidated; cookies and automatic redirects are disabled.
- Foundry and search credentials are attached only to their separate API requests.
- Only HTML/plain text is supported; extraction is bounded and sources are marked untrusted.
- Exact quotations, source IDs, evidence IDs, and question IDs are checked in code.
- There are four tool names. No shell, write-to-third-party, or publishing tools exist.
- Search/page attempts consume their counters even when network requests fail.
- Rejected or repeated calls still consume the global step budget.
- PostgreSQL foreign keys protect evidence/source relationships; step IDs are unique per run.
- A dedicated non-pooled session holds the per-run advisory lock. An optimistic
  version check also rejects stale checkpoint writes.

These are integrity guarantees, not a truth guarantee. A quote can be authentic
but irrelevant; two URLs can repeat the same underlying source; documentation
can be outdated. Host allowlists must contain trusted public documentation
domains. This sample does not implement DNS pinning or a general-purpose
internet crawler. Source text is retained in the database and local reports;
choose public sources and apply database access/retention controls accordingly.

External model/search calls are **at least once** across a crash before commit.
A resume can repeat the uncommitted request and its cost. Completed checkpoints
are not repeated. No end-to-end exactly-once or zero-cost retry claim is made.

## Tests

```powershell
dotnet test BoundedResearchAgent.slnx
# Use a dedicated, initialized-by-the-tests PostgreSQL database:
$env:RESEARCH_TEST_POSTGRES = 'Host=localhost;Port=55432;Database=research_tests;Username=research;Password=YOUR-LOCAL-PASSWORD'
dotnet test BoundedResearchAgent.slnx
```

Create `research_tests` first. The test role must be allowed to create the
`research` schema and its tables and have `CREATEDB` for migration-recovery tests.
Checkpoint tests append uniquely identified runs without deleting existing rows.
Migration-recovery tests create and drop their own uniquely named databases.
PostgreSQL tests are reported as skipped when
the test connection variable is absent. See [validation](docs/VALIDATION.md) for
what was actually executed and [evaluation](docs/EVALUATION.md) for the distinction
between host tests and research-quality evaluation.

## License

MIT. See [LICENSE](LICENSE). Fetched third-party pages retain their own licenses.

## Contributing

Keep source admission, evidence validation, and checkpoint behavior explicit.
Add focused tests for changes to tools, persistence, or completion policy. Add
live-run evidence before claiming a model-quality improvement.

## References

- [Microsoft Foundry chat-completions REST API](https://learn.microsoft.com/en-us/azure/foundry/openai/latest)
- [Serper Search API](https://serper.dev/playground)
- [Npgsql usage and transactions](https://www.npgsql.org/doc/basic-usage.html)
- [PostgreSQL container image and volume layout](https://hub.docker.com/_/postgres)
