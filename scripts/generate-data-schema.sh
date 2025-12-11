#!/bin/bash

# generate-data-schema.sh
# Generates docs/data-schema.md from the codebase
# Run from repository root: ./scripts/generate-data-schema.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_FILE="$REPO_ROOT/docs/data-schema.md"
MODELS_DIR="$REPO_ROOT/src/SentenceStudio/Models"
DATA_DIR="$REPO_ROOT/src/SentenceStudio/Data"

echo "🔍 Generating data schema documentation..."
echo "   Models directory: $MODELS_DIR"
echo "   Output file: $OUTPUT_FILE"

# Get current date
CURRENT_DATE=$(date +%Y-%m-%d)

# Start building the output
cat > "$OUTPUT_FILE" << 'HEADER'
# SentenceStudio Data Schema

> **Purpose**: This document describes the data structure of the SentenceStudio language learning application. It's designed to be shared with LLMs for context about the application's data model.
>
HEADER

echo "> **Last Generated**: $CURRENT_DATE" >> "$OUTPUT_FILE"

cat >> "$OUTPUT_FILE" << 'HEADER2'
>
> **Database**: SQLite via Entity Framework Core

---

## Entity Relationship Diagram

```mermaid
erDiagram
    VocabularyWord ||--o| VocabularyProgress : "tracks learning"
    VocabularyWord ||--o{ ResourceVocabularyMapping : "belongs to"
    VocabularyWord ||--o{ PlacementTestItem : "tested in"
    
    LearningResource ||--o{ ResourceVocabularyMapping : "contains"
    LearningResource ||--o{ VocabularyLearningContext : "context source"
    
    VocabularyProgress ||--o{ VocabularyLearningContext : "has attempts"
    
    UserProfile ||--o{ DailyPlanCompletion : "has plan items"
    UserProfile ||--o{ UserActivity : "has activities"
    
    PlacementTest ||--o{ PlacementTestItem : "contains"
    
    Conversation ||--o{ ConversationChunk : "contains"
    
    SkillProfile ||--o{ LearningResource : "categorizes"
HEADER2

# Extract entity properties from model files and add to diagram
echo "" >> "$OUTPUT_FILE"

# Function to extract class properties
extract_entity() {
    local file=$1
    local class_name=$2
    
    if [ -f "$file" ]; then
        echo "    $class_name {" >> "$OUTPUT_FILE"
        
        # Extract properties with their types
        grep -E "^\s+public\s+(int|string|bool|float|double|DateTime|Guid)" "$file" 2>/dev/null | \
        grep -v "//" | \
        head -6 | \
        while read -r line; do
            # Parse: public Type Name { get; set; }
            type=$(echo "$line" | sed -E 's/.*public\s+([a-zA-Z0-9?]+)\s+.*/\1/' | sed 's/?//')
            name=$(echo "$line" | sed -E 's/.*public\s+[a-zA-Z0-9?]+\s+([a-zA-Z0-9]+)\s+.*/\1/')
            
            # Add PK/FK annotations
            annotations=""
            if [ "$name" = "Id" ]; then
                annotations=" PK"
            elif [[ "$name" == *"Id" ]] && [ "$name" != "Id" ]; then
                annotations=" FK"
            fi
            
            echo "        $type $name$annotations" >> "$OUTPUT_FILE"
        done
        
        echo "    }" >> "$OUTPUT_FILE"
        echo "" >> "$OUTPUT_FILE"
    fi
}

# Extract key entities
extract_entity "$MODELS_DIR/VocabularyWord.cs" "VocabularyWord"
extract_entity "$MODELS_DIR/VocabularyProgress.cs" "VocabularyProgress"
extract_entity "$MODELS_DIR/LearningResource.cs" "LearningResource"
extract_entity "$MODELS_DIR/ResourceVocabularyMapping.cs" "ResourceVocabularyMapping"
extract_entity "$MODELS_DIR/VocabularyLearningContext.cs" "VocabularyLearningContext"
extract_entity "$MODELS_DIR/DailyPlanCompletion.cs" "DailyPlanCompletion"
extract_entity "$MODELS_DIR/UserProfile.cs" "UserProfile"

echo '```' >> "$OUTPUT_FILE"

# Add entity documentation sections
cat >> "$OUTPUT_FILE" << 'ENTITIES_HEADER'

---

## Core Entities

ENTITIES_HEADER

# Function to generate entity documentation
generate_entity_doc() {
    local file=$1
    local class_name=$2
    local purpose=$3
    
    if [ ! -f "$file" ]; then
        echo "⚠️  Warning: $file not found"
        return
    fi
    
    echo "### $class_name" >> "$OUTPUT_FILE"
    echo "**Purpose**: $purpose" >> "$OUTPUT_FILE"
    echo "" >> "$OUTPUT_FILE"
    echo "**Source**: \`${file#$REPO_ROOT/}\`" >> "$OUTPUT_FILE"
    echo "" >> "$OUTPUT_FILE"
    echo "| Property | Type | Description |" >> "$OUTPUT_FILE"
    echo "|----------|------|-------------|" >> "$OUTPUT_FILE"
    
    # Extract properties
    grep -E "^\s+public\s+" "$file" 2>/dev/null | \
    grep -E "\s+(get|set)" | \
    grep -v "//" | \
    while read -r line; do
        # Parse property
        type=$(echo "$line" | sed -E 's/.*public\s+([a-zA-Z0-9<>?,\[\]]+)\s+.*/\1/')
        name=$(echo "$line" | sed -E 's/.*public\s+[a-zA-Z0-9<>?,\[\]]+\s+([a-zA-Z0-9]+)\s+.*/\1/')
        
        # Skip navigation properties and complex types for now
        if [[ "$type" != *"List"* ]] && [[ "$type" != *"ICollection"* ]]; then
            # Generate description based on name
            desc="-"
            case "$name" in
                "Id") desc="Primary key" ;;
                *"Id") desc="Foreign key reference" ;;
                "CreatedAt") desc="Record creation timestamp" ;;
                "UpdatedAt") desc="Last modification timestamp" ;;
                "MasteryScore") desc="Learning mastery level (0.0-1.0)" ;;
                "CurrentStreak") desc="Consecutive correct answers" ;;
                "NextReviewDate") desc="SRS next review date" ;;
                "IsCompleted") desc="Completion status" ;;
            esac
            
            echo "| $name | \`$type\` | $desc |" >> "$OUTPUT_FILE"
        fi
    done
    
    echo "" >> "$OUTPUT_FILE"
    echo "---" >> "$OUTPUT_FILE"
    echo "" >> "$OUTPUT_FILE"
}

# Generate documentation for key entities
generate_entity_doc "$MODELS_DIR/VocabularyWord.cs" "VocabularyWord" "Stores individual vocabulary terms being learned"
generate_entity_doc "$MODELS_DIR/VocabularyProgress.cs" "VocabularyProgress" "Tracks learning progress using spaced repetition"
generate_entity_doc "$MODELS_DIR/LearningResource.cs" "LearningResource" "Content containers for vocabulary and media"
generate_entity_doc "$MODELS_DIR/ResourceVocabularyMapping.cs" "ResourceVocabularyMapping" "Links vocabulary words to resources"
generate_entity_doc "$MODELS_DIR/VocabularyLearningContext.cs" "VocabularyLearningContext" "Records individual practice attempts"
generate_entity_doc "$MODELS_DIR/DailyPlanCompletion.cs" "DailyPlanCompletion" "Tracks daily learning plan items"
generate_entity_doc "$MODELS_DIR/UserProfile.cs" "UserProfile" "User settings and preferences"
generate_entity_doc "$MODELS_DIR/UserActivity.cs" "UserActivity" "Activity log for analytics"

# Add enums section
cat >> "$OUTPUT_FILE" << 'ENUMS_SECTION'

## Enums

ENUMS_SECTION

# Find and document enums
for enum_file in "$MODELS_DIR"/*.cs; do
    if grep -q "public enum" "$enum_file" 2>/dev/null; then
        enum_name=$(grep "public enum" "$enum_file" | head -1 | sed -E 's/.*public enum\s+([a-zA-Z0-9]+).*/\1/')
        if [ -n "$enum_name" ]; then
            echo "### $enum_name" >> "$OUTPUT_FILE"
            echo '```' >> "$OUTPUT_FILE"
            # Extract enum values
            sed -n '/public enum '"$enum_name"'/,/^}/p' "$enum_file" | \
            grep -E "^\s+[A-Z]" | \
            sed 's/,$//' | \
            tr -d ' ' | \
            tr '\n' ', ' | \
            sed 's/,$/\n/' >> "$OUTPUT_FILE"
            echo '```' >> "$OUTPUT_FILE"
            echo "" >> "$OUTPUT_FILE"
        fi
    fi
done

# Add business logic section
cat >> "$OUTPUT_FILE" << 'BUSINESS_LOGIC'

---

## Business Logic Reference

### Mastery Scoring
- **Known threshold**: MasteryScore ≥ 0.85 AND ProductionInStreak ≥ 2
- **Production weight**: Production attempts (typing/speaking) count more than recognition
- **Streak reset**: Any incorrect answer resets CurrentStreak and ProductionInStreak to 0

### Spaced Repetition (SM-2)
- **Initial interval**: 1 day
- **EaseFactor range**: 1.3 to 2.5+ (lower = harder word)
- **Interval calculation**: `ReviewInterval * EaseFactor` after correct answer
- **Due for review**: `NextReviewDate <= DateTime.UtcNow`

### Smart Resources
- **DailyReview**: Auto-generated list of words due for SRS review
- **NewWords**: Recently added words not yet practiced
- **Struggling**: Words with low mastery or frequently incorrect

---

## File Locations

| Category | Path |
|----------|------|
| DbContext | `src/SentenceStudio/Data/AppDbContext.cs` |
| Entity Models | `src/SentenceStudio/Models/` |
| Repositories | `src/SentenceStudio/Data/` |
| Migrations | `src/SentenceStudio/Migrations/` |
| Services | `src/SentenceStudio/Services/` |

---

*This document is auto-generated. Run `scripts/generate-data-schema.sh` to update.*
BUSINESS_LOGIC

echo "✅ Generated $OUTPUT_FILE"
echo ""
echo "📊 Summary:"
echo "   - Entity models found: $(find "$MODELS_DIR" -name "*.cs" 2>/dev/null | wc -l | tr -d ' ')"
echo "   - Migrations found: $(find "$REPO_ROOT/src/SentenceStudio/Migrations" -name "*.cs" 2>/dev/null | wc -l | tr -d ' ')"
echo ""
echo "💡 To view the ER diagram, paste the Mermaid code into:"
echo "   - https://mermaid.live"
echo "   - VS Code with Mermaid extension"
echo "   - GitHub markdown preview"
