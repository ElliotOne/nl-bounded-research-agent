# Evaluate the research agent

There are two different questions: does the host enforce its contract, and does
the model produce good research? Passing the first does not answer the second.

## Deterministic host tests

The tests use clearly synthetic source text and scripted model-selected tools.
They check function-call parsing, request shape, quote and citation integrity,
URL/redirect admission, tool budgets, coverage, cached reads, interruptions,
decision replay, database rollback, stale versions, and worker ownership.
They also cover reading sections beyond the former page prefix, cached offset
reads, bounded full snapshots, retrying premature completion, nonblocking
limitations, genuine unresolved gaps, compacted model context without changing
the audit transcript, and legacy completion/replay behavior.
Passage-ID tests verify that the host copies exact stored text and rejects
invented IDs or IDs from a different source.

The question-only test loads the shipped brief with no seeds, searches, reads,
records evidence, searches for a second topic, reads, records, and finishes.
It exercises the real web adapter through synthetic HTTP responses and verifies
that API credentials are absent from source-page requests. Other tests reject
reading an allowed but undiscovered URL and treating a search result as evidence
without reading it. A seeded brief can also finish without a search quota.
These checks verify supported control flow, not a live model's query choices or
research quality. Citation-normalization tests preserve historical replay output.

## Research quality

`data/evaluation/research-cases.json` defines a small manual evaluation set.
For a live model evaluation:

1. Freeze the brief, model deployment, configuration, prompt/tool version, and
   source snapshots. Record the run UUID and evaluation date.
2. Have a reviewer label each final claim as supported, partially supported,
   unsupported, contradicted, or unverifiable from its cited excerpts in source
   context. Split compound claims when different clauses require different evidence.
3. Record required-question coverage, supported-claim fraction, unresolved
   conflicts, discovery metrics below, model usage, duration, and terminal status.
4. Compare against a fixed retrieval-and-answer baseline using the same question
   and admissible source material. A fully frozen corpus is needed for a fair
   comparison; changing live search results confounds the comparison.
5. Review whether the agent requests completion too early or exhausts its budget
   repeating work. Keep incomplete-but-honest outcomes distinct from false answers.

Two distinct URLs satisfy only the host's URL-diversity check. They do not prove
source independence. Exact-quote matching verifies provenance of text, not
semantic support. Model confidence is not used as a correctness gate.

## Measure discovery separately from completion

For each run, record:

| Measure | What to inspect |
| --- | --- |
| Search attempts and redundant queries | `steps` with `tool_name=SearchWeb`, including rejected attempts |
| Distinct URLs read | `sources.url`; cached window reads are not new sources |
| Distinct URLs read outside seeds | Source URLs absent from `brief.seedUrls` |
| Distinct URLs cited outside seeds | Final finding evidence IDs joined to evidence and source URLs, excluding seeds |
| Rejected tools | Error results in the ordered tool ledger |
| Claim-support labels | Reviewer judgments using cited quotes and surrounding source context |
| Outcome and usage | Status, stop reason, required gaps, caveats, duration, reported tokens |

A search that returns only an already-read seed adds no newly discovered source.
A new URL that is never read or cited contributes no evidence to the final report.
Conversely, a well-supported seeded run need not search merely to increase a
counter. Compare discovery behavior using the question-only case, and use the
seeded case to check that the agent avoids unnecessary work.

For the HybridCache brief, review `RemoveAsync` and `RemoveByTagAsync` separately.
A passage about stampede coordination cannot support an invalidation claim.
An authentic quote explaining local caching alone does not establish every
clause of a claim about shared backend behavior. Label these findings partially
supported or unsupported rather than letting host completion imply semantic approval.

Captured-decision `replay` checks the same host decisions again from stored
tool calls and results. It does not re-run the model or constitute a fresh
model-quality evaluation. To test a new model, start new live research runs and
label the resulting evidence and findings.

No aggregate research-quality score is claimed in this repository. The validation
record includes completed seeded investigations and an earlier incomplete run
that read a discovered non-seed page. The revised question-only default has
deterministic offline coverage; it was not re-run against paid APIs for this
revision. Model/version comparisons and cost claims require a labeled evaluation
set and measurements beyond these captures.
