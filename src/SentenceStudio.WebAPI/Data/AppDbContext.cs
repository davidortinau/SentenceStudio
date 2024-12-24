using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SentenceStudio.WebAPI.Models;

namespace SentenceStudio.WebAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
            InitializeDatabase();
        }

    public DbSet<VocabularyList> VocabularyLists { get; set; }
    public DbSet<VocabularyWord> VocabularyWords { get; set; }

    internal void InitializeDatabase()
    {
        // need to do thsi for each entity I'll be syncing
        const string datasyncTrigger = @"
            CREATE OR REPLACE FUNCTION {0}_datasync() RETURNS trigger AS $$
            BEGIN
                NEW.""UpdatedAt"" = NOW() AT TIME ZONE 'UTC';
                NEW.""Version"" = convert_to(gen_random_uuid()::text, 'UTF8');
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE OR REPLACE TRIGGER
                {0}_datasync
            BEFORE INSERT OR UPDATE ON
                ""{0}""
            FOR EACH ROW EXECUTE PROCEDURE
                {0}_datasync();
        ";

        Database.EnsureCreated();
        ExecuteRawSqlOnEachEntity(@"DELETE FROM ""{0}""");
        ExecuteRawSqlOnEachEntity(datasyncTrigger);
    }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<VocabularyList>()
                    .Property(m => m.UpdatedAt).HasDefaultValueSql("NOW() AT TIME ZONE 'UTC'");
                    
            modelBuilder.Entity<VocabularyList>()
                .HasMany(vl => vl.Words)
                .WithMany(vw => vw.VocabularyLists)
                .UsingEntity<Dictionary<string, object>>(
                    "VocabularyListVocabularyWord",
                    j => j.HasOne<VocabularyWord>().WithMany().HasForeignKey("VocabularyWordId"),
                    j => j.HasOne<VocabularyList>().WithMany().HasForeignKey("VocabularyListId"));

            modelBuilder.Entity<VocabularyWord>()
                .Property(m => m.UpdatedAt).HasDefaultValueSql("NOW() AT TIME ZONE 'UTC'");
        }

        protected void ExecuteRawSqlOnEachEntity(string format)
    {
        foreach (IEntityType table in Model.GetEntityTypes())
        {
            string sql = string.Format(format, table.GetTableName());
            Database.ExecuteSqlRaw(sql);
        }
    }
}
