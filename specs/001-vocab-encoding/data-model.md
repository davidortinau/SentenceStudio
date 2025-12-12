# Data Model: Vocabulary Encoding Enhancements

**Feature**: 001-vocab-encoding  
**Created**: 2025-12-11  
**Purpose**: Define entity schemas, relationships, indexes, and migrations for vocabulary encoding features

## Entity Schemas

### VocabularyWord (Extended)

**Table**: `VocabularyWord` (existing, extend with new columns)

```csharp
[Table("VocabularyWord")]
public partial class VocabularyWord : ObservableObject
{
    // === EXISTING FIELDS ===
    public int Id { get; set; }
    
    [ObservableProperty]
    private string? nativeLanguageTerm;
    
    [ObservableProperty]
    private string? targetLanguageTerm;
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Existing navigation properties
    [JsonIgnore]
    public List<LearningResource> LearningResources { get; set; } = new();
    
    [JsonIgnore]
    public List<ResourceVocabularyMapping> ResourceMappings { get; set; } = new();
    
    // === NEW FIELDS (Migration Required) ===
    
    /// <summary>
    /// Dictionary form / lemma for inflected words (e.g., "가다" for "가면")
    /// </summary>
    [Description("The dictionary form or lemma of the word, for grouping inflected forms")]
    [ObservableProperty]
    private string? lemma;
    
    /// <summary>
    /// Comma-separated tags for categorization (e.g., "nature,season,visual")
    /// Maximum: 10 tags, each up to 50 characters
    /// </summary>
    [Description("Comma-separated tags for categorizing vocabulary words")]
    [ObservableProperty]
    [MaxLength(500)] // 10 tags * 50 chars
    private string? tags;
    
    /// <summary>
    /// Mnemonic story or memory association (e.g., "단풍 sounds like 'don't pong'")
    /// </summary>
    [Description("Silly story or memory association to aid recall")]
    [ObservableProperty]
    [MaxLength(1000)]
    private string? mnemonicText;
    
    /// <summary>
    /// Optional image URI for mnemonic visualization
    /// </summary>
    [Description("Image URL to visualize the mnemonic or concept")]
    [ObservableProperty]
    [MaxLength(2000)] // URLs can be long
    private string? mnemonicImageUri;
    
    /// <summary>
    /// Audio pronunciation URI (existing concept, adding for completeness)
    /// </summary>
    [ObservableProperty]
    [MaxLength(2000)]
    private string? audioPronunciationUri;
    
    // === NEW NAVIGATION PROPERTY ===
    
    /// <summary>
    /// Example sentences demonstrating word usage in context
    /// </summary>
    [JsonIgnore]
    public List<ExampleSentence> ExampleSentences { get; set; } = new();
    
    // === NOT MAPPED (Derived) ===
    
    /// <summary>
    /// Encoding strength score (0-1.0) - calculated, not persisted
    /// </summary>
    [NotMapped]
    public double EncodingStrength { get; set; }
    
    /// <summary>
    /// Encoding strength label (Basic/Good/Strong) - calculated, not persisted
    /// </summary>
    [NotMapped]
    public string EncodingStrengthLabel { get; set; } = "Basic";
}
```

**Validation Rules**:
- `Tags`: Max 10 tags, validated by UI layer (split by comma, limit count)
- `MnemonicText`: Max 1000 characters (enforced by database constraint)
- `MnemonicImageUri`: Must be valid URL format if provided (UI validation)
- `Lemma`: Optional; typically same language as `TargetLanguageTerm`

**Indexes**:
```sql
-- Enable tag filtering (User Story 3)
CREATE INDEX IX_VocabularyWord_Tags ON VocabularyWord(Tags);

-- Enable lemma search/grouping (User Story 4)
CREATE INDEX IX_VocabularyWord_Lemma ON VocabularyWord(Lemma);
```

---

### ExampleSentence (New Entity)

**Table**: `ExampleSentence` (new table, create via migration)

```csharp
[Table("ExampleSentence")]
public class ExampleSentence
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Foreign key to VocabularyWord
    /// </summary>
    public int VocabularyWordId { get; set; }
    
    /// <summary>
    /// Optional foreign key to LearningResource (source attribution)
    /// </summary>
    public int? LearningResourceId { get; set; }
    
    /// <summary>
    /// Example sentence in target language
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string TargetSentence { get; set; } = string.Empty;
    
    /// <summary>
    /// Translation in native language
    /// </summary>
    [MaxLength(500)]
    public string? NativeSentence { get; set; }
    
    /// <summary>
    /// Audio URI for sentence pronunciation
    /// </summary>
    [MaxLength(2000)]
    public string? AudioUri { get; set; }
    
    /// <summary>
    /// Flag indicating this is a primary teaching example
    /// </summary>
    public bool IsCore { get; set; }
    
    /// <summary>
    /// Timestamps
    /// </summary>
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // === NAVIGATION PROPERTIES ===
    
    /// <summary>
    /// Parent vocabulary word
    /// </summary>
    [JsonIgnore]
    public VocabularyWord? VocabularyWord { get; set; }
    
    /// <summary>
    /// Optional source learning resource
    /// </summary>
    [JsonIgnore]
    public LearningResource? LearningResource { get; set; }
}
```

**Validation Rules**:
- `TargetSentence`: Required, max 500 characters
- `VocabularyWordId`: Must reference existing VocabularyWord (foreign key constraint)
- `LearningResourceId`: Optional, must reference existing LearningResource if provided
- `AudioUri`: Optional, validated as URL format if provided

**Indexes**:
```sql
-- Foreign key lookup (required for joins and counts)
CREATE INDEX IX_ExampleSentence_VocabularyWordId ON ExampleSentence(VocabularyWordId);

-- Filter by core examples (User Story 2)
CREATE INDEX IX_ExampleSentence_IsCore ON ExampleSentence(IsCore);

-- Composite index for resource-specific queries
CREATE INDEX IX_ExampleSentence_VocabId_IsCore ON ExampleSentence(VocabularyWordId, IsCore);

-- Optional: Learning resource attribution lookup
CREATE INDEX IX_ExampleSentence_LearningResourceId ON ExampleSentence(LearningResourceId);
```

---

## Relationships

```
VocabularyWord (1) -----> (N) ExampleSentence
    |
    |----> (N) LearningResource (many-to-many via ResourceVocabularyMapping)
    |
    └----> (1) VocabularyProgress

ExampleSentence (N) -----> (1) VocabularyWord (required)
ExampleSentence (N) -----> (0..1) LearningResource (optional)
```

**Cascade Behavior**:
- Delete `VocabularyWord` → Cascade delete associated `ExampleSentence` records
- Delete `LearningResource` → Set `LearningResourceId` to NULL in `ExampleSentence` records

---

## Database Migrations

### Migration 1: Add Encoding Fields to VocabularyWord

**File**: `AddVocabularyEncodingFields.cs`

```csharp
public partial class AddVocabularyEncodingFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add new columns to VocabularyWord table
        migrationBuilder.AddColumn<string>(
            name: "Lemma",
            table: "VocabularyWord",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Tags",
            table: "VocabularyWord",
            type: "TEXT",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MnemonicText",
            table: "VocabularyWord",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MnemonicImageUri",
            table: "VocabularyWord",
            type: "TEXT",
            maxLength: 2000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AudioPronunciationUri",
            table: "VocabularyWord",
            type: "TEXT",
            maxLength: 2000,
            nullable: true);

        // Create indexes for filtering/searching
        migrationBuilder.CreateIndex(
            name: "IX_VocabularyWord_Tags",
            table: "VocabularyWord",
            column: "Tags");

        migrationBuilder.CreateIndex(
            name: "IX_VocabularyWord_Lemma",
            table: "VocabularyWord",
            column: "Lemma");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_VocabularyWord_Tags",
            table: "VocabularyWord");

        migrationBuilder.DropIndex(
            name: "IX_VocabularyWord_Lemma",
            table: "VocabularyWord");

        migrationBuilder.DropColumn(name: "Lemma", table: "VocabularyWord");
        migrationBuilder.DropColumn(name: "Tags", table: "VocabularyWord");
        migrationBuilder.DropColumn(name: "MnemonicText", table: "VocabularyWord");
        migrationBuilder.DropColumn(name: "MnemonicImageUri", table: "VocabularyWord");
        migrationBuilder.DropColumn(name: "AudioPronunciationUri", table: "VocabularyWord");
    }
}
```

### Migration 2: Create ExampleSentence Table

**File**: `CreateExampleSentenceTable.cs`

```csharp
public partial class CreateExampleSentenceTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Create ExampleSentence table
        migrationBuilder.CreateTable(
            name: "ExampleSentence",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                VocabularyWordId = table.Column<int>(type: "INTEGER", nullable: false),
                LearningResourceId = table.Column<int>(type: "INTEGER", nullable: true),
                TargetSentence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                NativeSentence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                AudioUri = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                IsCore = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExampleSentence", x => x.Id);
                table.ForeignKey(
                    name: "FK_ExampleSentence_VocabularyWord_VocabularyWordId",
                    column: x => x.VocabularyWordId,
                    principalTable: "VocabularyWord",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ExampleSentence_LearningResource_LearningResourceId",
                    column: x => x.LearningResourceId,
                    principalTable: "LearningResource",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        // Create indexes for performance
        migrationBuilder.CreateIndex(
            name: "IX_ExampleSentence_VocabularyWordId",
            table: "ExampleSentence",
            column: "VocabularyWordId");

        migrationBuilder.CreateIndex(
            name: "IX_ExampleSentence_IsCore",
            table: "ExampleSentence",
            column: "IsCore");

        migrationBuilder.CreateIndex(
            name: "IX_ExampleSentence_VocabId_IsCore",
            table: "ExampleSentence",
            columns: new[] { "VocabularyWordId", "IsCore" });

        migrationBuilder.CreateIndex(
            name: "IX_ExampleSentence_LearningResourceId",
            table: "ExampleSentence",
            column: "LearningResourceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ExampleSentence");
    }
}
```

---

## State Transitions

### Encoding Strength Lifecycle

```
New Vocabulary Word
    ↓
[Basic] (0-33%: Only TargetLanguageTerm + NativeLanguageTerm)
    ↓
User adds Mnemonic or Tags
    ↓
[Good] (34-66%: 3-4 encoding fields present)
    ↓
User adds Image, Audio, Example Sentences
    ↓
[Strong] (67-100%: 5-6 encoding fields present)
```

**Triggers**:
- Encoding strength recalculates on any change to: Tags, Mnemonic, Image, Audio, ExampleSentence count
- Calculation is real-time (not persisted); no database triggers required

### Example Sentence Lifecycle

```
Created (TargetSentence required)
    ↓
Audio Generated (optional) → AudioUri populated
    ↓
Marked as Core (optional) → IsCore = true
    ↓
Updated/Deleted by user
```

**Triggers**:
- Audio generation initiated when user clicks audio button (UI action)
- Core flag toggled via edit UI
- Deletion cascades from VocabularyWord deletion

---

## Query Patterns (Optimized)

### Pattern 1: Tag Filtering with Compiled Query

```csharp
// Compiled once, reused many times
private static readonly Func<ApplicationDbContext, string, IAsyncEnumerable<VocabularyWord>> 
    _filterByTagCompiled = EF.CompileAsyncQuery(
        (ApplicationDbContext db, string tag) =>
            db.VocabularyWords
                .Where(w => EF.Functions.Like(w.Tags, $"%{tag}%"))
                .OrderBy(w => w.TargetLanguageTerm));
```

### Pattern 2: Batch Load Example Sentence Counts (Avoid N+1)

```csharp
// Load words
var words = await db.VocabularyWords
    .Skip(skip)
    .Take(pageSize)
    .ToListAsync();

var wordIds = words.Select(w => w.Id).ToList();

// Batch load counts in single query
var sentenceCounts = await db.ExampleSentences
    .Where(es => wordIds.Contains(es.VocabularyWordId))
    .GroupBy(es => es.VocabularyWordId)
    .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);

// Apply counts in-memory
foreach (var word in words)
{
    var count = sentenceCounts.GetValueOrDefault(word.Id, 0);
    word.EncodingStrength = _calculator.Calculate(word, count);
}
```

### Pattern 3: Core Example Sentences for Word Detail

```csharp
// Single query with index on (VocabularyWordId, IsCore)
var coreExamples = await db.ExampleSentences
    .Where(es => es.VocabularyWordId == wordId && es.IsCore)
    .OrderBy(es => es.CreatedAt)
    .ToListAsync();
```

---

## Performance Benchmarks (Target)

| Operation | Target | With Optimization |
|-----------|--------|-------------------|
| Filter 5000 words by tag | <50ms | Index on Tags |
| Load 50 words with example counts | <100ms | Batch query, no N+1 |
| Calculate encoding for 100 words | <30ms | In-memory, cached counts |
| Load core examples for word detail | <20ms | Composite index |
| Insert new example sentence | <10ms | Standard EF write |

**Test Environment**: Mid-range Android device (Snapdragon 660, Android API 28)

---

## CoreSync Considerations

**Tables Requiring Sync**:
- `VocabularyWord` (existing, add new columns to sync schema)
- `ExampleSentence` (new table, add to CoreSync configuration)

**Sync Configuration**:
```csharp
// In CoreSync setup
modelBuilder.Entity<VocabularyWord>().ToTable("VocabularyWord");
modelBuilder.Entity<ExampleSentence>().ToTable("ExampleSentence");
```

**Conflict Resolution**: Last-write-wins (default CoreSync behavior)

---

## Summary

- **2 migrations**: Add encoding fields to VocabularyWord, create ExampleSentence table
- **6 indexes**: Tags, Lemma, VocabularyWordId, IsCore, composite, resource attribution
- **0 triggers**: Encoding strength calculated in-memory, not persisted
- **2 new navigation properties**: VocabularyWord.ExampleSentences, ExampleSentence.VocabularyWord
- **Performance focus**: Compiled queries, batch loading, focused indexes for mobile optimization
