using Npgsql;

namespace SentenceStudio.Api.Startup;

/// <summary>Test-host guard for the actual startup connection, before its first write.</summary>
internal interface IStartupConnectionAttestor
{
    void ValidateBeforeOpen(NpgsqlConnection connection);

    Task AttestOpenAsync(NpgsqlConnection connection, CancellationToken cancellationToken);
}
