using SQLite;

namespace Plugin.Maui.HelpKit.Storage.Migrations;

/// <summary>
/// V1 — initial schema. Creates all day-one tables declared as
/// <c>[Table]</c> entities in <c>HelpKitEntities.cs</c>.
/// </summary>
internal sealed class V001_InitialSchema : IHelpKitMigration
{
    public int Version => 1;

    public void Apply(SQLiteConnection db)
    {
        db.CreateTable<SchemaVersionRow>();
        db.CreateTable<ConversationRow>();
        db.CreateTable<MessageRow>();
        db.CreateTable<IngestionFingerprintRow>();
        db.CreateTable<AnswerCacheRow>();
    }
}
