using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Web;
using Npgsql;
using NpgsqlTypes;

namespace BoundedResearchAgent.Persistence;

public sealed class PostgresResearchStore(NpgsqlConnection connection) : IResearchStore
{
    public async Task MigrateAsync(string directory, CancellationToken token)
    {
        await using var transaction = await connection.BeginTransactionAsync(token);
        await ExecuteAsync("SELECT pg_advisory_xact_lock(440050); CREATE SCHEMA IF NOT EXISTS research; CREATE TABLE IF NOT EXISTS research.schema_versions(version text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());", token);
        foreach (var path in Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal))
        {
            var sql = await File.ReadAllTextAsync(path, token);
            var hash = SourcePolicy.Hash(sql);
            await using var check = new NpgsqlCommand("SELECT sha256 FROM research.schema_versions WHERE version=$1", connection);
            check.Parameters.AddWithValue(Path.GetFileName(path));
            var previous = await check.ExecuteScalarAsync(token) as string;
            if (previous is not null)
            {
                if (previous != hash) throw new InvalidOperationException("Applied migration was modified. Add a new migration instead.");
                continue;
            }
            if (Path.GetFileName(path) != "001_initial.sql" || !await InitialSchemaBaseline.TryAdoptAsync(connection, sql, token))
                await ExecuteAsync(sql, token);
            await using var insert = new NpgsqlCommand("INSERT INTO research.schema_versions(version,sha256) VALUES($1,$2)", connection);
            insert.Parameters.AddWithValue(Path.GetFileName(path));
            insert.Parameters.AddWithValue(hash);
            await insert.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task LockAsync(Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended($1,44))", connection);
        command.Parameters.AddWithValue(id.ToString());
        if (await command.ExecuteScalarAsync(token) is not true) throw new InvalidOperationException("Another worker owns this run.");
        // Lock is released when this dedicated session is closed by the CLI.
    }

    public async Task CreateAsync(ResearchRun run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO research.runs(id,status,question,started_at,version,state) VALUES($1,$2,$3,$4,0,$5)", connection);
        command.Parameters.AddWithValue(run.Id);
        command.Parameters.AddWithValue(run.Status);
        command.Parameters.AddWithValue(run.Brief.Question);
        command.Parameters.AddWithValue(run.StartedAtUtc);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, Json.Write(run));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ResearchRun> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT state::text FROM research.runs WHERE id=$1", connection);
        command.Parameters.AddWithValue(id);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new InvalidOperationException("Research run was not found.");
        return Json.Read<ResearchRun>(json);
    }

    public async Task SaveAsync(ResearchRun run, CancellationToken cancellationToken)
    {
        var version = run.Version;
        var snapshot = Json.Read<ResearchRun>(Json.Write(run));
        snapshot.Version = version + 1;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new NpgsqlCommand("UPDATE research.runs SET status=$1,state=$2,version=version+1,updated_at=now() WHERE id=$3 AND version=$4", connection);
        update.Parameters.AddWithValue(run.Status);
        update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, Json.Write(snapshot));
        update.Parameters.AddWithValue(run.Id);
        update.Parameters.AddWithValue(version);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Checkpoint version conflict.");
        foreach (var source in run.Sources)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO research.sources VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(run_id,source_id) DO NOTHING", connection);
            cmd.Parameters.AddWithValue(run.Id);
            cmd.Parameters.AddWithValue(source.Id);
            cmd.Parameters.AddWithValue(source.Url);
            cmd.Parameters.AddWithValue(source.RetrievedAtUtc);
            cmd.Parameters.AddWithValue(source.Sha256);
            cmd.Parameters.AddWithValue(source.Text);
            cmd.Parameters.AddWithValue(source.Truncated);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var evidence in run.Evidence)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO research.evidence VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(run_id,evidence_id) DO NOTHING", connection);
            cmd.Parameters.AddWithValue(run.Id);
            cmd.Parameters.AddWithValue(evidence.Id);
            cmd.Parameters.AddWithValue(evidence.QuestionId);
            cmd.Parameters.AddWithValue(evidence.SourceId);
            cmd.Parameters.AddWithValue(evidence.Quote);
            cmd.Parameters.AddWithValue(evidence.Claim);
            cmd.Parameters.AddWithValue(evidence.Stance);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var step in run.Steps)
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO research.steps VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(run_id,step_number) DO NOTHING", connection);
            cmd.Parameters.AddWithValue(run.Id);
            cmd.Parameters.AddWithValue(step.Number);
            cmd.Parameters.AddWithValue(step.Tool);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, step.Arguments.ToJsonString());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, step.Result);
            cmd.Parameters.AddWithValue(step.Tokens);
            cmd.Parameters.AddWithValue(step.AtUtc);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        run.Version = snapshot.Version;
    }

    private async Task ExecuteAsync(string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
