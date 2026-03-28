# Skill: SQLite Migration Generation (Multi-TFM Project)

## When to Use

When you need to generate EF Core migrations for the SQLite provider in the `SentenceStudio.Shared` project, which targets multiple TFMs (net10.0, net10.0-ios, net10.0-android, net10.0-maccatalyst).

## Problem

`dotnet ef migrations add` fails with `ResolvePackageAssets` errors on multi-TFM projects. The DesignTimeDbContextFactory only supports PostgreSQL by default.

## Steps

1. **Backup** both files:
   ```bash
   cp src/SentenceStudio.Shared/SentenceStudio.Shared.csproj src/SentenceStudio.Shared/SentenceStudio.Shared.csproj.bak
   cp src/SentenceStudio.Shared/Data/DesignTimeDbContextFactory.cs src/SentenceStudio.Shared/Data/DesignTimeDbContextFactory.cs.bak
   ```

2. **Temp-modify csproj**: Change `<TargetFrameworks>` to single `<TargetFramework>net10.0</TargetFramework>`. Remove the conditional `<ItemGroup>` blocks. Add `<Compile Remove="Migrations\*.cs" />` to exclude PostgreSQL migrations.

3. **Temp-modify DesignTimeDbContextFactory**: Add `--provider` argument parsing to switch between `UseSqlite("Data Source=:memory:")` and `UseNpgsql(...)`.

4. **Run migration tool**:
   ```bash
   dotnet ef migrations add <Name> \
     --project src/SentenceStudio.Shared/SentenceStudio.Shared.csproj \
     --startup-project src/SentenceStudio.Api/SentenceStudio.Api.csproj \
     --context ApplicationDbContext \
     --output-dir Migrations/Sqlite \
     -- --provider Sqlite
   ```

5. **Restore** original files:
   ```bash
   mv src/SentenceStudio.Shared/SentenceStudio.Shared.csproj.bak src/SentenceStudio.Shared/SentenceStudio.Shared.csproj
   mv src/SentenceStudio.Shared/Data/DesignTimeDbContextFactory.cs.bak src/SentenceStudio.Shared/Data/DesignTimeDbContextFactory.cs
   ```

6. **CRITICAL: Revert PostgreSQL snapshot** — the tool may corrupt it:
   ```bash
   git checkout -- src/SentenceStudio.Shared/Migrations/ApplicationDbContextModelSnapshot.cs
   ```

7. **Review generated migration**: The tool may detect snapshot drift from hand-crafted migrations. Remove redundant operations and keep only the intended changes.

8. **Restore packages**: `dotnet restore src/SentenceStudio.Shared/SentenceStudio.Shared.csproj`

## Pitfalls

- The tool WILL corrupt `Migrations/ApplicationDbContextModelSnapshot.cs` (PostgreSQL) — always revert it.
- If the snapshot already includes the column you're trying to add, the tool generates an empty or unrelated migration — add your operations manually.
- SQLite has no conditional DDL — use `pragma_table_info` checks in C# (SyncService) rather than in the migration SQL.
