# Database contract

Use a dedicated PostgreSQL database. The sample uses ordinary PostgreSQL and
Npgsql; pgvector and a separate cache are unnecessary for this workload.

| Table | Purpose |
| --- | --- |
| `research.schema_versions` | Applied migration filename, content hash, timestamp |
| `research.runs` | Run identity, question, lifecycle status, version, JSONB checkpoint |
| `research.sources` | Run-scoped source identity, URL, fetched text, time, hash, truncation |
| `research.evidence` | Exact excerpt, model claim, stance, required-question ID, source FK |
| `research.steps` | Ordered native tool selections, arguments, tool results, token usage |

The JSONB checkpoint retains the exact conversation and typed run state needed
for resume. Relational source/evidence/step rows make inspection possible with
SQL and enforce source foreign keys and unique run-scoped identities. These
projections and the checkpoint commit in the same transaction.

Source rows retain the full bounded snapshot rather than only the current
model-visible window. The checkpoint also stores section offsets, contract
version, and finish-attempt count. Model requests compact older page windows;
the stored conversation and source text remain available for inspection and
captured-decision replay. No schema change is needed for these JSONB fields.

```sql
SELECT id, status, version, started_at,
       state->>'stopReason' AS stop_reason,
       state->>'mode' AS mode
FROM research.runs ORDER BY started_at DESC;

SELECT e.question_id, e.evidence_id, e.stance, e.claim, e.quote, s.url,
       s.retrieved_at, s.content_sha256, s.truncated
FROM research.evidence e
JOIN research.sources s USING (run_id, source_id)
WHERE e.run_id = 'REPLACE-WITH-RUN-UUID'
ORDER BY e.evidence_id;

SELECT step_number, tool_name, arguments, result, total_tokens
FROM research.steps
WHERE run_id = 'REPLACE-WITH-RUN-UUID'
ORDER BY step_number;
```

## Checkpoint transaction

`SaveAsync` compares `runs.version` with the loaded version. If it differs, the
write fails. The transaction then inserts any new snapshots, evidence, and step
rows. An insert failure rolls back the run-state update too. The in-memory
version advances only after commit.

Previously inserted rows are append-only through this application. The database
role can still mutate records directly; this is not a tamper-proof audit store.
Restrict database access and back up the database if it holds valuable runs.
There is no automatic retention deletion in this sample.

## Worker ownership

The CLI takes a session advisory lock derived from the run UUID before loading
or continuing an existing run. A second worker fails immediately. Connections
are deliberately non-pooled so closing the dedicated connection releases the
session lock. Migrations use a separate fixed transaction advisory lock.

## Local setup and operations

Compose binds to loopback, requires a password, and retains data in a named
volume. PostgreSQL 18 mounts that volume at `/var/lib/postgresql`; an existing
PostgreSQL 17 instance is also supported. The sample does not change an existing
server's authentication or databases.

An existing database and an existing empty `research` schema are supported;
`init-db` creates the project's tables inside that schema. PostgreSQL error
`28P01` means the server rejected the password before any schema access. Check
the username and password in `RESEARCH_Database__ConnectionString` against the
same host and port in psql or pgAdmin. Recreating the schema will not fix an
authentication failure. The CLI reports SQLSTATE codes and actionable messages
without printing passwords, connection strings, or server row details.

If initial tables were created manually without the `001_initial.sql` history
entry, initialization verifies their definitions against a reference schema
created within the migration transaction. Matching definitions are adopted by
recording the unchanged migration's hash. Existing tables and rows are left
intact. Partial tables, differing columns, constraints, or indexes cause a
rollback rather than an assumed migration success. Only the temporary reference
namespace is removed; applied migration files retain their original contents
and checksum checks. This compatibility check requires permission to create
the reference schema in the database.

For a remote server, supply its connection string and require validated TLS
according to your deployment's certificate configuration. Keep passwords outside
the checked-in configuration. Run `init-db` with an appropriate migration role;
the CLI runtime needs SELECT/INSERT/UPDATE on this schema and database connectivity.

`docker compose stop` stops the local container while retaining data. A future
major PostgreSQL upgrade requires the normal database upgrade/backup procedure;
changing an image major version is not a migration strategy.
