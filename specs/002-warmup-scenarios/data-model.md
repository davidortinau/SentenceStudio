# Data Model: Warmup Conversation Scenarios

**Feature**: 002-warmup-scenarios  
**Date**: 2026-01-24

## Entity Relationship Diagram

```
┌─────────────────────────┐       ┌─────────────────────────┐
│  ConversationScenario   │       │      Conversation       │
├─────────────────────────┤       ├─────────────────────────┤
│ Id (PK)                 │◄──────│ ScenarioId (FK, nullable)│
│ Name                    │  1:N  │ Id (PK)                 │
│ NameKorean              │       │ CreatedAt               │
│ PersonaName             │       └─────────────┬───────────┘
│ PersonaDescription      │                     │
│ SituationDescription    │                     │ 1:N
│ ConversationType        │                     ▼
│ QuestionBank            │       ┌─────────────────────────┐
│ IsPredefined            │       │   ConversationChunk     │
│ CreatedAt               │       ├─────────────────────────┤
│ UpdatedAt               │       │ Id (PK)                 │
└─────────────────────────┘       │ ConversationId (FK)     │
                                  │ Text                    │
                                  │ Author                  │
                                  │ Role                    │
                                  │ Comprehension           │
                                  │ SentTime                │
                                  └─────────────────────────┘
```

## Entities

### ConversationScenario (NEW)

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | `int` | PK, Auto-increment | Unique identifier |
| `Name` | `string` | Required, Max 100 | Display name (English) |
| `NameKorean` | `string` | Optional, Max 100 | Display name (Korean) |
| `PersonaName` | `string` | Required, Max 50 | Name of AI persona (e.g., "김철수") |
| `PersonaDescription` | `string` | Required, Max 500 | Persona backstory/role |
| `SituationDescription` | `string` | Required, Max 500 | Context for the conversation |
| `ConversationType` | `ConversationType` | Required | OpenEnded or Finite |
| `QuestionBank` | `string` | Optional | Scenario-specific questions (newline-separated) |
| `IsPredefined` | `bool` | Default: false | True for system scenarios (read-only) |
| `CreatedAt` | `DateTime` | Required | Creation timestamp |
| `UpdatedAt` | `DateTime` | Required | Last modification timestamp |

### Conversation (MODIFIED)

| Field | Type | Change | Description |
|-------|------|--------|-------------|
| `ScenarioId` | `int?` | **NEW** | FK to ConversationScenario (nullable for backward compat) |

### ConversationType (NEW Enum)

```csharp
public enum ConversationType
{
    /// <summary>Conversation continues until user ends it</summary>
    OpenEnded = 0,
    
    /// <summary>Conversation concludes when transactional goal is achieved</summary>
    Finite = 1
}
```

## Predefined Scenarios (Seed Data)

| Name | PersonaName | PersonaDescription | ConversationType |
|------|-------------|-------------------|------------------|
| First Meeting | 김철수 | 25-year-old drama writer from Seoul | OpenEnded |
| Ordering Coffee | 박지영 | Friendly barista at a local cafe | Finite |
| Ordering Dinner | 이민호 | Waiter at a Korean BBQ restaurant | Finite |
| Asking for Directions | 최수진 | Helpful stranger on the street | Finite |
| Weekend Plans | 김하나 | Curious friend asking about your plans | OpenEnded |

## Validation Rules

### ConversationScenario

1. `Name` must not be empty and must be unique per user (predefined scenarios share namespace)
2. `PersonaName` must not be empty
3. `PersonaDescription` must not be empty
4. `SituationDescription` must not be empty
5. `IsPredefined` scenarios cannot be deleted or modified by users
6. `UpdatedAt` must be >= `CreatedAt`

### Business Rules

1. A `Conversation` can have at most one `ConversationScenario`
2. If `ScenarioId` is null, default "First Meeting" scenario behavior applies
3. Users can only delete/edit scenarios where `IsPredefined = false`
4. Predefined scenarios cannot be duplicated directly (user creates new custom scenario inspired by predefined)

## Migration Strategy

### AddConversationScenario Migration

```csharp
// 1. Create ConversationScenarios table
migrationBuilder.CreateTable(
    name: "ConversationScenarios",
    columns: table => new
    {
        Id = table.Column<int>(nullable: false)
            .Annotation("Sqlite:Autoincrement", true),
        Name = table.Column<string>(maxLength: 100, nullable: false),
        NameKorean = table.Column<string>(maxLength: 100, nullable: true),
        PersonaName = table.Column<string>(maxLength: 50, nullable: false),
        PersonaDescription = table.Column<string>(maxLength: 500, nullable: false),
        SituationDescription = table.Column<string>(maxLength: 500, nullable: false),
        ConversationType = table.Column<int>(nullable: false),
        QuestionBank = table.Column<string>(nullable: true),
        IsPredefined = table.Column<bool>(nullable: false, defaultValue: false),
        CreatedAt = table.Column<DateTime>(nullable: false),
        UpdatedAt = table.Column<DateTime>(nullable: false)
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_ConversationScenarios", x => x.Id);
    });

// 2. Add ScenarioId FK to Conversations table
migrationBuilder.AddColumn<int>(
    name: "ScenarioId",
    table: "Conversations",
    nullable: true);

migrationBuilder.CreateIndex(
    name: "IX_Conversations_ScenarioId",
    table: "Conversations",
    column: "ScenarioId");

migrationBuilder.AddForeignKey(
    name: "FK_Conversations_ConversationScenarios_ScenarioId",
    table: "Conversations",
    column: "ScenarioId",
    principalTable: "ConversationScenarios",
    principalColumn: "Id",
    onDelete: ReferentialAction.SetNull);
```

## CoreSync Considerations

- `ConversationScenario` should sync across devices (user's custom scenarios)
- Predefined scenarios should NOT sync (each device seeds its own)
- Add `[Table("ConversationScenarios")]` attribute for CoreSync compatibility
- Consider adding `IsDeleted` soft-delete flag if needed for sync conflict resolution
