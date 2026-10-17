using System.Net.Sockets;
using BoundedResearchAgent.App;
using Npgsql;

namespace BoundedResearchAgent.Tests;

public sealed class DatabaseErrorsTests
{
    [Theory]
    [InlineData("28P01", "Password authentication failed")]
    [InlineData("3D000", "database does not exist")]
    [InlineData("42501", "Permission denied")]
    [InlineData("42P01", "Run init-db")]
    [InlineData("42P07", "migration history")]
    public void Server_errors_give_actionable_diagnostics_without_echoing_server_values(string sqlState, string expected)
    {
        var error = new PostgresException("sensitive-server-message", "FATAL", "FATAL", sqlState,
            detail: "sensitive-row-values", hint: "sensitive-server-hint");

        var result = DatabaseErrors.Describe(error);

        Assert.Contains(sqlState, result);
        Assert.Contains(expected, result);
        Assert.DoesNotContain("sensitive", result);
    }

    [Fact]
    public void Unknown_server_errors_report_the_code_without_echoing_values()
    {
        var result = DatabaseErrors.Describe(new PostgresException("sensitive-value", "ERROR", "ERROR", "22023"));

        Assert.Contains("22023", result);
        Assert.DoesNotContain("sensitive-value", result);
    }

    [Fact]
    public void Connection_failure_reports_socket_reason_without_echoing_connection_text()
    {
        var error = new NpgsqlException("sensitive-connection-text", new IOException("sensitive-inner-text",
            new SocketException((int)SocketError.ConnectionRefused)));

        var result = DatabaseErrors.Describe(error);

        Assert.Contains("ConnectionRefused", result);
        Assert.DoesNotContain("sensitive", result);
    }

    [Fact]
    public void Timeout_reports_action_without_echoing_inner_message()
    {
        var result = DatabaseErrors.Describe(new NpgsqlException("sensitive-value", new TimeoutException("sensitive-value")));

        Assert.Contains("timed out", result);
        Assert.DoesNotContain("sensitive", result);
    }
}
