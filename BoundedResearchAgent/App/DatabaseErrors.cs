using System.Net.Sockets;
using Npgsql;

namespace BoundedResearchAgent.App;

public static class DatabaseErrors
{
    public static string Describe(NpgsqlException error)
    {
        if (error is PostgresException postgres)
        {
            var action = postgres.SqlState switch
            {
                "28P01" => "Password authentication failed. Check Username and Password in RESEARCH_Database__ConnectionString.",
                "28000" => "The server rejected authentication. Check the database user and server authentication rules.",
                "3D000" => "The database does not exist. Check Database in RESEARCH_Database__ConnectionString.",
                "42501" => "Permission denied. The database user needs access to the research schema and its tables.",
                "42P01" => "A required table is missing. Run init-db against the same database first.",
                "42P07" => "A table or index already exists. Check the research schema and migration history for a conflict.",
                "42703" => "A required column is missing. Check that the research tables match this project's migrations.",
                "53300" => "The server has reached its connection limit. Retry after connections are available.",
                "57P03" => "The server is not ready to accept connections. Retry when PostgreSQL is ready.",
                _ => "The server rejected the operation. Check PostgreSQL server logs for details."
            };
            // Server messages and details can contain SQL values. Report only the code and a known explanation.
            return $"PostgreSQL error {postgres.SqlState}: {action}";
        }

        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is TimeoutException)
                return "PostgreSQL operation timed out. Check the configured host, port, and server availability.";
            if (cause is SocketException socket)
                return $"PostgreSQL connection failed ({socket.SocketErrorCode}). Check Host, Port, and that PostgreSQL is running.";
        }

        return "PostgreSQL connection or transport failed. Check server availability, TLS settings, and PostgreSQL server logs.";
    }
}
