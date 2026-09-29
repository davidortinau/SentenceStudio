using Npgsql;

namespace SentenceStudio.WebApp.Tests.Infrastructure;

public sealed class WebAppPostgresServerSafetyTests
{
    [Fact]
    public void A_disposable_server_requires_an_explicit_connection_and_per_run_owner_ID()
    {
        var owner = Guid.NewGuid().ToString("N");
        const string local = "Host=127.0.0.1;Database=postgres;Username=fixture";

        WebAppPostgresServer.ValidateConfiguration(null, owner).Should().NotBeNull();
        WebAppPostgresServer.ValidateConfiguration(local, null).Should().NotBeNull();
        WebAppPostgresServer.ValidateConfiguration(local, "stale-owner").Should().NotBeNull();
        WebAppPostgresServer.ValidateConfiguration(local, owner).Should().BeNull();
    }

    [Theory]
    [InlineData("Host=production.example.invalid;Database=postgres;Username=fixture")]
    [InlineData("Host=localhost;Database=sentencestudio;Username=fixture")]
    public void An_unknown_host_or_a_developer_database_is_rejected_before_connecting(string connection)
    {
        WebAppPostgresServer.ValidateConfiguration(connection, Guid.NewGuid().ToString("N"))
            .Should().Contain("loopback connection");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("webapp_it_unregistered")]
    public void An_existing_or_unregistered_database_cannot_be_used_by_the_fixture(string database)
    {
        Action connect = () => WebAppPostgresServer.ConnectionStringFor(database);

        connect.Should().Throw<InvalidOperationException>()
            .WithMessage("*created in this process*");
    }

    [WebAppPostgresFact]
    public async Task The_migration_connection_must_be_attested_on_its_own_database()
    {
        var database = await WebAppPostgresServer.CreateDatabaseAsync("attestation");
        var connectionString = WebAppPostgresServer.ConnectionStringFor(database);
        var guard = new WebAppPostgresMigrationGuard(database);

        await using (var owned = new NpgsqlConnection(connectionString))
        {
            await owned.OpenAsync();
            await WebAppPostgresServer.AssertOwnedDatabaseAsync(owned, database);
            await using var command = owned.CreateCommand();
            command.CommandText = "SELECT 1";
            await guard.NonQueryExecutingAsync(command, null!, default);
        }

        var maintenance = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres"
        }.ConnectionString;
        await using var wrongDatabase = new NpgsqlConnection(maintenance);
        await wrongDatabase.OpenAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WebAppPostgresServer.AssertOwnedDatabaseAsync(wrongDatabase, database));
        failure.Message.Should().Contain("not on the owned disposable cluster and database");

        await using var migrationCommand = wrongDatabase.CreateCommand();
        migrationCommand.CommandText = "CREATE DATABASE \"fixture_must_not_run\"";
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await guard.NonQueryExecutingAsync(migrationCommand, null!, default);
        });
        blocked.Message.Should().Contain("not on the owned disposable cluster and database");
    }
}
