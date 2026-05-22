using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests;

public class MigrationDiscoveryTests
{
    [Fact]
    public void RefreshTokenReplacementMigration_IsDiscoverable()
    {
        using var db = new ApplicationDbContext();

        var migrations = db.Database.GetMigrations();

        migrations.Should().Contain("20260503221947_AddRefreshTokenReplacedBy",
            "the refresh-token schema fix must be discovered by EF so Postgres login/register can issue refresh tokens");
    }
}
