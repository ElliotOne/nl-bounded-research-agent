using System.Text.Json.Nodes;
using BoundedResearchAgent.Agent;
using BoundedResearchAgent.Domain;
using BoundedResearchAgent.Persistence;
using BoundedResearchAgent.Web;
using Npgsql;

namespace BoundedResearchAgent.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESEARCH_TEST_POSTGRES")))
            Skip = "Set RESEARCH_TEST_POSTGRES to a dedicated PostgreSQL test database.";
    }
}

public sealed class PostgresTests
{
    private static async Task<NpgsqlConnection> Connect()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RESEARCH_TEST_POSTGRES")) { Pooling = false };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [PostgresFact]
    public async Task Migrations_are_repeatable_and_full_agent_run_is_durable_in_postgres()
    {
        await using var connection = await Connect();
        var store = new PostgresResearchStore(connection);
        await store.MigrateAsync(Path.Combine(AppContext.BaseDirectory, "database"), default);
        await store.MigrateAsync(Path.Combine(AppContext.BaseDirectory, "database"), default);
        var run = Samples.Run();
        await store.CreateAsync(run, default);
        await store.LockAsync(run.Id, default);
        var result = await new ResearchEngine(new ScriptedModel(Samples.HappyPath()), new FixtureWeb(), store).ContinueAsync(run);
        var loaded = await store.LoadAsync(run.Id, default);
        Assert.Equal("completed", loaded.Status);
        Assert.Equal(result.Version, loaded.Version);
        await using var query = new NpgsqlCommand("SELECT (SELECT count(*) FROM research.sources WHERE run_id=$1), (SELECT count(*) FROM research.evidence WHERE run_id=$1), (SELECT count(*) FROM research.steps WHERE run_id=$1)", connection);
        query.Parameters.AddWithValue(run.Id);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(2, reader.GetInt64(1));
        Assert.Equal(6, reader.GetInt64(2));
    }

    [PostgresFact]
    public async Task Invalid_evidence_rolls_back_checkpoint_and_preserves_version()
    {
        await using var connection = await Connect();
        var store = new PostgresResearchStore(connection);
        await store.MigrateAsync(Path.Combine(AppContext.BaseDirectory, "database"), default);
        var run = Samples.Run();
        await store.CreateAsync(run, default);
        run.Evidence.Add(new Evidence("E001", "Q1", "missing-source", Samples.Text1, "Invalid FK", "supports"));
        await Assert.ThrowsAsync<PostgresException>(() => store.SaveAsync(run, default));
        Assert.Equal(0, run.Version);
        Assert.Empty((await store.LoadAsync(run.Id, default)).Evidence);
    }

    [PostgresFact]
    public async Task Stale_checkpoint_cannot_overwrite_newer_state()
    {
        await using var connection = await Connect();
        var store = new PostgresResearchStore(connection);
        await store.MigrateAsync(Path.Combine(AppContext.BaseDirectory, "database"), default);
        var run = Samples.Run();
        await store.CreateAsync(run, default);
        var stale = await store.LoadAsync(run.Id, default);
        await store.SaveAsync(run, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(stale, default));
        Assert.Equal(1, (await store.LoadAsync(run.Id, default)).Version);
    }

    [PostgresFact]
    public async Task Another_worker_cannot_lock_the_same_run()
    {
        await using var first = await Connect();
        await using var second = await Connect();
        var id = Guid.NewGuid();
        await new PostgresResearchStore(first).LockAsync(id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresResearchStore(second).LockAsync(id, default));
    }

    [PostgresFact]
    public async Task Matching_existing_tables_get_migration_history_without_losing_a_run()
    {
        await InFreshDatabase(async connection =>
        {
            await Execute(connection, await InitialSql());
            var store = new PostgresResearchStore(connection);
            var run = Samples.Run();
            await store.CreateAsync(run, default);
            var result = await new ResearchEngine(new ScriptedModel(Samples.HappyPath()), new FixtureWeb(), store).ContinueAsync(run);

            await store.MigrateAsync(MigrationDirectory, default);
            await store.MigrateAsync(MigrationDirectory, default);

            var loaded = await store.LoadAsync(run.Id, default);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Json.Write(result)), JsonNode.Parse(Json.Write(loaded))));
            await using var history = new NpgsqlCommand("SELECT sha256 FROM research.schema_versions WHERE version='001_initial.sql'", connection);
            Assert.Equal(SourcePolicy.Hash(await InitialSql()), await history.ExecuteScalarAsync());
            await using var rows = new NpgsqlCommand("SELECT (SELECT count(*) FROM research.sources), (SELECT count(*) FROM research.evidence), (SELECT count(*) FROM research.steps)", connection);
            await using (var reader = await rows.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(2, reader.GetInt64(0));
                Assert.Equal(2, reader.GetInt64(1));
                Assert.Equal(6, reader.GetInt64(2));
            }
            await AssertNoReferenceSchemas(connection);
        });
    }

    [PostgresFact]
    public async Task Incompatible_existing_columns_are_rejected_without_recording_history()
    {
        await InFreshDatabase(async connection =>
        {
            await Execute(connection, await InitialSql());
            await Execute(connection, "ALTER TABLE research.evidence ALTER COLUMN quote TYPE varchar(1000)");
            await AssertBaselineRejected(connection);
        });
    }

    [PostgresFact]
    public async Task Missing_foreign_key_is_rejected_without_recording_history()
    {
        await InFreshDatabase(async connection =>
        {
            await Execute(connection, await InitialSql());
            await Execute(connection, "ALTER TABLE research.evidence DROP CONSTRAINT evidence_run_id_source_id_fkey");
            await AssertBaselineRejected(connection);
        });
    }

    [PostgresFact]
    public async Task Partial_existing_schema_is_rejected_without_creating_missing_tables()
    {
        await InFreshDatabase(async connection =>
        {
            var sql = await InitialSql();
            await Execute(connection, sql[..sql.IndexOf("CREATE INDEX", StringComparison.Ordinal)]);
            await AssertBaselineRejected(connection);
            await using var check = new NpgsqlCommand("SELECT to_regclass('research.sources') IS NULL", connection);
            Assert.Equal(true, await check.ExecuteScalarAsync());
        });
    }

    private static string MigrationDirectory => Path.Combine(AppContext.BaseDirectory, "database");
    private static Task<string> InitialSql() => File.ReadAllTextAsync(Path.Combine(MigrationDirectory, "001_initial.sql"));

    private static async Task AssertBaselineRejected(NpgsqlConnection connection)
    {
        var store = new PostgresResearchStore(connection);
        var run = Samples.Run();
        await store.CreateAsync(run, default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.MigrateAsync(MigrationDirectory, default));
        Assert.Contains("do not match", error.Message);
        Assert.Equal(run.Id, (await store.LoadAsync(run.Id, default)).Id);
        await using var check = new NpgsqlCommand("SELECT to_regclass('research.schema_versions') IS NULL", connection);
        Assert.Equal(true, await check.ExecuteScalarAsync());
        await AssertNoReferenceSchemas(connection);
    }

    private static async Task AssertNoReferenceSchemas(NpgsqlConnection connection)
    {
        await using var check = new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'research_baseline_%'", connection);
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InFreshDatabase(Func<NpgsqlConnection, Task> test)
    {
        await using var admin = await Connect();
        var name = $"research_migration_test_{Guid.NewGuid():N}";
        await Execute(admin, $"CREATE DATABASE {name}");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name, Pooling = false };
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            await test(connection);
        }
        finally
        {
            // Only remove the unique database created by this test.
            await Execute(admin, $"DROP DATABASE {name}");
        }
    }
}
