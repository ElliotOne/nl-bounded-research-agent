CREATE SCHEMA IF NOT EXISTS research;
CREATE TABLE research.runs (
    id uuid PRIMARY KEY,
    status text NOT NULL CHECK (status IN ('running','completed','incomplete','failed')),
    question text NOT NULL,
    started_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    version integer NOT NULL DEFAULT 0 CHECK (version >= 0),
    state jsonb NOT NULL CHECK (jsonb_typeof(state) = 'object')
);
CREATE INDEX runs_status_started ON research.runs(status, started_at DESC);
CREATE TABLE research.sources (
    run_id uuid NOT NULL REFERENCES research.runs(id),
    source_id text NOT NULL,
    url text NOT NULL,
    retrieved_at timestamptz NOT NULL,
    content_sha256 text NOT NULL CHECK (length(content_sha256) = 64),
    body_text text NOT NULL,
    truncated boolean NOT NULL,
    PRIMARY KEY (run_id, source_id),
    UNIQUE (run_id, url)
);
CREATE TABLE research.evidence (
    run_id uuid NOT NULL,
    evidence_id text NOT NULL,
    question_id text NOT NULL,
    source_id text NOT NULL,
    quote text NOT NULL CHECK (length(quote) BETWEEN 20 AND 1000),
    claim text NOT NULL,
    stance text NOT NULL CHECK (stance IN ('supports','contradicts')),
    PRIMARY KEY (run_id, evidence_id),
    FOREIGN KEY (run_id, source_id) REFERENCES research.sources(run_id, source_id)
);
CREATE TABLE research.steps (
    run_id uuid NOT NULL REFERENCES research.runs(id),
    step_number integer NOT NULL CHECK (step_number > 0),
    tool_name text NOT NULL,
    arguments jsonb NOT NULL,
    result jsonb NOT NULL,
    total_tokens integer NOT NULL CHECK (total_tokens >= 0),
    at_utc timestamptz NOT NULL,
    PRIMARY KEY (run_id, step_number)
);
