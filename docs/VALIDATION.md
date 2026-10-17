# Validation record

Validation covers the implementation and the captured runs described in Issue #50.

- .NET SDK 10.0.101; build completed with zero warnings and zero errors.
- `dotnet format --verify-no-changes` passed after formatting the companion source.
- Local isolated PostgreSQL 17.6 cluster on loopback, separate from the installed service.
- 76 unit and real PostgreSQL integration tests passed, with zero failures and zero skips.
- Integration checks cover migration repeatability, transactional source/evidence/step
  persistence, foreign-key failure rollback, stale versions, and competing workers.
- Migration recovery was checked in fresh databases: matching existing tables
  retained a completed run, source/evidence/step rows, and gained migration history;
  differing column types, a missing foreign key, and partial tables were rejected
  with no history entry or leftover reference schema.
- Test investigation content and tool choices are synthetic, explicitly in the test project.
- Completion regressions cover later-page sections, cached continuation reads,
  exact quotations copied by passage ID, invented/cross-source passage rejection,
  retrying premature completion, optional caveats, genuine unresolved gaps,
  context compaction without changing the stored transcript, and legacy replay.
- The revised question-only sample loads without seeds and completes a synthetic
  seven-step search/read/evidence/search/read/evidence/finish investigation through
  the real web adapter and HTTP test handlers. No external requests are made.
  Additional tests reject undiscovered reads and unread search-result evidence,
  allow sufficient seeded research without a search quota, and normalize duplicate
  citations for contract version 3 without changing version 2 replay output.
- Foundry request construction and native tool-call parsing are tested through HTTP test handlers.
- Serper search tests cover POST endpoint and authentication, scoped JSON queries,
  organic-result parsing, exact-host admission, deduplication, configured result
  limits, empty/malformed responses, HTTP errors, and source-request credential isolation.
- The actual CLI `init-db`, `report`, and `replay` commands were exercised against
  the test database. Captured-decision replay reproduced the synthetic test report.
- Live validation used the configured Foundry deployment and real documentation
  pages. Serper searches were exercised during investigation. Two intermediate
  live attempts exhausted the 100000-token accounting threshold before meeting
  the citation gate; their incomplete records remain intact.
- Seeded live run `f7818394-edce-4f0e-a2cc-d2006d295d6d` completed with
  `coverage_gate_passed`: 10 steps, 2 page fetches, 4 evidence records, 56890
  reported tokens, no rejected tools, and no unresolved required questions.
  It read two official seed URLs and needed no search calls. Both full source
  snapshots fit the storage bound (20840 and 45288 characters).
- A read-only audit of that live run passed 96 checks against the actual
  database: export/checkpoint/relational agreement, exact passage quotations,
  native call IDs, hashes, citations, required-question coverage, counters,
  migration checksum, constraints, and indexes. Aggregate consistency checks
  across all four stored runs found no violations.
- A subsequent captured live run, `a521fe38-0184-4c45-bd48-c871647f1e8e`, completed
  in 13 steps, 1 search, 2 page fetches, 4 evidence records, and 80249 reported
  tokens. Its search returned an existing seed; neither read source was outside
  its two-seed brief. One invented passage ID was rejected before correction.
  Its read-only database audit passed 111 consistency checks. The submitted
  report repeated a citation ID within one finding; new contract version 3
  normalizes this while historical version 2 replay retains it.
- Earlier live run `d5afe769-14eb-4ee9-9732-45fce36da31c` read its single seed,
  searched, and read a non-seed Microsoft documentation page. It stopped after
  6 steps and 30368 reported tokens at its then-configured 30000-token threshold,
  with 2 sources and 3 evidence records. It demonstrates live discovery beyond
  seeds, not completed research under the revised default.
- Offline in-memory replay reproduced four captured reports (two completed and
  two incomplete), using their persisted contract versions, without further
  model/search requests or database writes. This confirms host reproducibility,
  not a general research-quality or model-accuracy score.
- No new paid Foundry/Serper requests were made for the question-only revision.
  Its offline coverage is not presented as completed live validation. Consistent
  storage and a passed citation gate do not establish semantic claim support.
- Docker Compose configuration is supplied for PostgreSQL 18; the Docker daemon was
  unavailable in this environment, so that container image was not started here.
  `docker compose config --quiet` passed with a temporary validation password.
- Newsletter frontmatter, JSX syntax, required sections, referenced image paths,
  and SVG/JSON syntax were checked. The cover and architecture graphic were
  visually inspected.
- The isolated test PostgreSQL server was stopped after validation; the existing
  installed PostgreSQL service was not changed.

Database diagnostics: eight regression cases passed for
authentication, missing database/table, permissions, migration conflicts,
unknown SQLSTATE codes, socket failures, and timeouts. Build and formatting
checks passed. An actual CLI `init-db` call with a deliberately invalid password
against the installed local server reported `28P01` and the corrective action,
without displaying credentials or changing database objects.

The test environment restored dependencies from the installed NuGet package
cache because outbound NuGet access was unavailable. Normal environments can
use the standard `dotnet restore` command.
