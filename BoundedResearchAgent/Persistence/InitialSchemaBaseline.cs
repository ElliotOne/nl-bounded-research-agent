using Npgsql;

namespace BoundedResearchAgent.Persistence;

internal static class InitialSchemaBaseline
{
    private static readonly string[] Tables = ["runs", "sources", "evidence", "steps"];

    public static async Task<bool> TryAdoptAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        var existing = await FingerprintAsync(connection, "research", token);
        if (existing.Length == 0) return false;

        // Build the checked-in schema in a unique namespace inside the migration transaction.
        // Compare definitions before recording history for any pre-existing tables.
        var reference = $"research_baseline_{Guid.NewGuid():N}";
        const string schemaStatement = "CREATE SCHEMA IF NOT EXISTS research;";
        if (!sql.Contains(schemaStatement, StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot verify the existing research schema against this initial migration.");
        var referenceSql = sql.Replace(schemaStatement, $"CREATE SCHEMA {reference};", StringComparison.Ordinal)
            .Replace("research.", $"{reference}.", StringComparison.Ordinal);
        await using (var create = new NpgsqlCommand(referenceSql, connection))
            await create.ExecuteNonQueryAsync(token);

        var expected = (await FingerprintAsync(connection, reference, token))
            .Replace($"{reference}.", "research.", StringComparison.Ordinal);
        if (existing != expected)
            throw new InvalidOperationException("Existing research tables do not match 001_initial.sql. Initialization was rolled back; inspect the table definitions and migration history.");

        // Only this newly created reference namespace is removed. User tables are never altered.
        await using var drop = new NpgsqlCommand($"DROP SCHEMA {reference} CASCADE", connection);
        await drop.ExecuteNonQueryAsync(token);
        return true;
    }

    private static async Task<string> FingerprintAsync(NpgsqlConnection connection, string schema, CancellationToken token)
    {
        const string query = """
            SELECT jsonb_build_object(
                'table', c.relname, 'kind', c.relkind, 'persistence', c.relpersistence,
                'rowSecurity', c.relrowsecurity, 'forceRowSecurity', c.relforcerowsecurity,
                'columns', (
                    SELECT jsonb_agg(jsonb_build_object(
                        'name', a.attname, 'type', format_type(a.atttypid, a.atttypmod),
                        'notNull', a.attnotnull, 'identity', a.attidentity, 'generated', a.attgenerated,
                        'default', pg_get_expr(d.adbin, d.adrelid)) ORDER BY a.attnum)
                    FROM pg_attribute a
                    LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                    WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped),
                'constraints', (
                    SELECT jsonb_agg(jsonb_build_object(
                        'definition', pg_get_constraintdef(k.oid, true), 'validated', k.convalidated)
                        ORDER BY pg_get_constraintdef(k.oid, true))
                    FROM pg_constraint k WHERE k.conrelid = c.oid),
                'indexes', (
                    SELECT jsonb_agg(jsonb_build_object(
                        'definition', pg_get_indexdef(i.indexrelid), 'valid', i.indisvalid,
                        'ready', i.indisready) ORDER BY pg_get_indexdef(i.indexrelid))
                    FROM pg_index i WHERE i.indrelid = c.oid))::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 AND c.relname = ANY($2)
            ORDER BY c.relname
            """;
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue(schema);
        command.Parameters.AddWithValue(Tables);
        var definitions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) definitions.Add(reader.GetString(0));
        return string.Join('\n', definitions);
    }
}
