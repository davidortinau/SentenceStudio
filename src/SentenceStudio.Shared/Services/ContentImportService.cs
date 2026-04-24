using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Abstractions;
using System.ComponentModel;
using System.Text.Json;
using Scriban;

namespace SentenceStudio.Services;

/// <summary>
/// Service for importing content (vocabulary, phrases, transcripts) from text or file sources.
/// MVP: Vocabulary only. Phrases and Transcripts deferred to v2.
/// </summary>
public interface IContentImportService
{
    /// <summary>
    /// Parse content from text or file input and return a preview of rows to import.
    /// </summary>
    /// <param name="request">The import request containing raw text, file bytes, format hints, and metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preview with parsed rows, detected format, and validation warnings.</returns>
    Task<ContentImportPreview> ParseContentAsync(ContentImportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Detect content type from raw content and optional format hint.
    /// MVP: Returns explicit type; heuristic body to be filled in Wave 2.
    /// </summary>
    /// <param name="content">Raw text content to classify.</param>
    /// <param name="formatHint">Optional format description from user.</param>
    /// <returns>Detection result with content type and confidence.</returns>
    ContentTypeDetectionResult DetectContentType(string content, string? formatHint);

    /// <summary>
    /// Commit the parsed import to the database. Creates or appends to a LearningResource,
    /// creates VocabularyWord rows with dedup, and creates ResourceVocabularyMapping rows.
    /// </summary>
    /// <param name="commit">The commit request containing preview, target resource, and dedup mode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with resource ID, counts (created/skipped/updated/failed), and warnings.</returns>
    Task<ContentImportResult> CommitImportAsync(ContentImportCommit commit, CancellationToken ct = default);
}

public class ContentImportService : IContentImportService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LearningResourceRepository _resourceRepo;
    private readonly ILogger<ContentImportService> _logger;
    private readonly IAiService _aiService;
    private readonly IFileSystemService _fileSystem;

    public ContentImportService(
        IServiceProvider serviceProvider,
        LearningResourceRepository resourceRepo,
        ILogger<ContentImportService> logger,
        IAiService aiService,
        IFileSystemService fileSystem)
    {
        _serviceProvider = serviceProvider;
        _resourceRepo = resourceRepo;
        _logger = logger;
        _aiService = aiService;
        _fileSystem = fileSystem;
    }


    public async Task<ContentImportPreview> ParseContentAsync(ContentImportRequest request, CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        ct.ThrowIfCancellationRequested();

        // MVP: Only vocabulary content type is implemented
        if (request.ContentType == ContentType.Phrases)
        {
            throw new NotSupportedException("Phrase import is not yet supported. Coming in v2.");
        }

        if (request.ContentType == ContentType.Transcript)
        {
            throw new NotSupportedException("Transcript import is not yet supported. Coming in v2.");
        }

        var rows = new List<ImportRow>();
        var warnings = new List<string>();

        // Extract raw content from either RawText or FileBytes
        string content;
        if (!string.IsNullOrEmpty(request.RawText))
        {
            content = request.RawText;
        }
        else if (request.FileBytes != null && request.FileBytes.Length > 0)
        {
            // For MVP, assume text files (CSV/TSV) encoded as UTF-8
            content = System.Text.Encoding.UTF8.GetString(request.FileBytes);
        }
        else
        {
            throw new ArgumentException("Either RawText or FileBytes must be provided.", nameof(request));
        }

        // Wave 2: Format detection
        var (formatType, delimiter) = DetectFormat(content, request.DelimiterOverride, request.FormatHint);
        
        _logger.LogDebug("Detected format: {FormatType}, Delimiter: {Delimiter}", formatType, delimiter?.ToString() ?? "N/A");

        // Parse based on detected format
        switch (formatType)
        {
            case "CSV":
            case "TSV":
            case "Pipe":
                rows = ParseDelimitedContent(content, delimiter!.Value, request.HasHeaderRow);
                break;

            case "JSON":
                rows = ParseJsonContent(content, request.HasHeaderRow);
                break;

            case "FreeText":
                // Cap size for AI processing (50KB = roughly 12,500 tokens)
                if (content.Length > 50_000)
                {
                    warnings.Add($"Content is too large for free-text extraction ({content.Length} chars). Please provide structured data or split into smaller chunks.");
                    rows.Add(new ImportRow
                    {
                        RowNumber = 1,
                        Status = RowStatus.Error,
                        Error = "Content exceeds size limit for free-text extraction (50KB)."
                    });
                }
                else
                {
                    try
                    {
                        rows = await ParseFreeTextContentAsync(content, request.TargetLanguage, request.NativeLanguage, request.FormatHint, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AI free-text extraction failed");
                        warnings.Add($"AI extraction failed: {ex.Message}");
                        rows.Add(new ImportRow
                        {
                            RowNumber = 1,
                            Status = RowStatus.Error,
                            Error = $"AI extraction failed: {ex.Message}. Please check your content or provide a structured format."
                        });
                    }
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown format type: {formatType}");
        }

        // Wave 2: Single-column translation
        var singleColumnRows = rows.Where(r => string.IsNullOrWhiteSpace(r.NativeLanguageTerm) && !string.IsNullOrWhiteSpace(r.TargetLanguageTerm)).ToList();
        if (singleColumnRows.Any())
        {
            _logger.LogInformation("Detected {Count} rows with missing native terms. Attempting AI translation...", singleColumnRows.Count);
            try
            {
                await TranslateMissingNativeTermsAsync(singleColumnRows, request.TargetLanguage, request.NativeLanguage, ct);
                // Mark AI-translated rows
                foreach (var row in singleColumnRows.Where(r => !string.IsNullOrWhiteSpace(r.NativeLanguageTerm)))
                {
                    row.IsAiTranslated = true;
                    if (row.Status == RowStatus.Warning && row.Error?.Contains("missing") == true)
                    {
                        row.Status = RowStatus.Ok;
                        row.Error = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI translation failed for single-column import");
                warnings.Add($"AI translation failed: {ex.Message}. Please provide translations manually.");
                // Keep rows as warnings — user can manually fill in translations
            }
        }

        // Detect format description
        var detectedFormat = formatType switch
        {
            "TSV" => "Tab-delimited (TSV)",
            "CSV" => "Comma-delimited (CSV)",
            "Pipe" => "Pipe-delimited",
            "JSON" => "JSON",
            "FreeText" => "Free-form text (AI-extracted)",
            _ => "Unknown"
        };

        // Detect content type (for MVP, explicit from request)
        var detectedContentType = new ContentTypeDetectionResult
        {
            ContentType = request.ContentType == ContentType.Auto ? ContentType.Vocabulary : request.ContentType,
            Confidence = 1.0f,
            Note = request.ContentType == ContentType.Auto
                ? "Auto-detected as vocabulary (based on MVP default)"
                : "Explicitly set by user"
        };

        return new ContentImportPreview
        {
            Rows = rows,
            DetectedFormat = detectedFormat,
            DetectedContentType = detectedContentType,
            Warnings = warnings
        };
    }

    private (string formatType, char? delimiter) DetectFormat(string content, char? delimiterOverride, string? formatHint)
    {
        // If delimiter is explicitly provided, use it
        if (delimiterOverride.HasValue)
        {
            return (delimiterOverride.Value switch
            {
                '\t' => "TSV",
                ',' => "CSV",
                '|' => "Pipe",
                _ => "CSV"
            }, delimiterOverride.Value);
        }

        // Try JSON first (most specific)
        if (content.TrimStart().StartsWith("[") || content.TrimStart().StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                return ("JSON", null);
            }
            catch
            {
                // Not valid JSON, continue to delimiter detection
            }
        }

        // Delimiter detection: count occurrences of comma, tab, pipe per line
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(10).ToList();
        if (lines.Count == 0)
        {
            return ("FreeText", null);
        }

        // Count delimiter occurrences per line
        var commaCount = new List<int>();
        var tabCount = new List<int>();
        var pipeCount = new List<int>();

        foreach (var line in lines)
        {
            commaCount.Add(CountDelimiterOccurrences(line, ','));
            tabCount.Add(CountDelimiterOccurrences(line, '\t'));
            pipeCount.Add(CountDelimiterOccurrences(line, '|'));
        }

        // Check for consistency (same count across at least 60% of lines)
        var commaConsistent = IsConsistent(commaCount);
        var tabConsistent = IsConsistent(tabCount);
        var pipeConsistent = IsConsistent(pipeCount);

        // Prefer tab, then pipe, then comma (comma is most ambiguous)
        if (tabConsistent && tabCount.Any(c => c > 0))
        {
            return ("TSV", '\t');
        }
        if (pipeConsistent && pipeCount.Any(c => c > 0))
        {
            return ("Pipe", '|');
        }
        if (commaConsistent && commaCount.Any(c => c > 0))
        {
            return ("CSV", ',');
        }

        // No clear delimiter found — fall back to free-text AI extraction
        return ("FreeText", null);
    }

    private int CountDelimiterOccurrences(string line, char delimiter)
    {
        if (delimiter == ',')
        {
            // Simple CSV quote handling: don't count commas inside quotes
            int count = 0;
            bool inQuotes = false;
            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    count++;
                }
            }
            return count;
        }
        else
        {
            return line.Count(c => c == delimiter);
        }
    }

    private bool IsConsistent(List<int> counts)
    {
        if (counts.Count == 0)
            return false;

        // Get the most common count
        var grouped = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (grouped == null || grouped.Key == 0)
            return false;

        // At least 60% of lines must have the same count
        return (double)grouped.Count() / counts.Count >= 0.6;
    }

    private List<ImportRow> ParseDelimitedContent(string content, char delimiter, bool hasHeaderRow)
    {
        var rows = new List<ImportRow>();
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // Skip header row if requested
        var dataLines = hasHeaderRow && lines.Length > 0
            ? lines.Skip(1).ToArray()
            : lines;

        for (int i = 0; i < dataLines.Length; i++)
        {
            var line = dataLines[i];
            var rowNumber = i + 1 + (hasHeaderRow ? 1 : 0); // Account for header in row numbering
            var parts = SplitLine(line, delimiter);

            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                // Skip empty rows
                continue;
            }

            string? targetTerm = parts.Length > 0 ? parts[0].Trim() : null;
            string? nativeTerm = parts.Length > 1 ? parts[1].Trim() : null;

            var rowStatus = RowStatus.Ok;
            string? error = null;

            if (string.IsNullOrEmpty(targetTerm))
            {
                rowStatus = RowStatus.Error;
                error = "Target language term is required.";
            }
            else if (string.IsNullOrEmpty(nativeTerm))
            {
                // Single-column case: AI translation will fill this in
                rowStatus = RowStatus.Warning;
                error = "Native language term missing (will use AI translation).";
            }

            rows.Add(new ImportRow
            {
                RowNumber = rowNumber,
                TargetLanguageTerm = targetTerm,
                NativeLanguageTerm = nativeTerm,
                Status = rowStatus,
                Error = error,
                IsSelected = rowStatus != RowStatus.Error // Auto-deselect error rows
            });
        }

        return rows;
    }

    private string[] SplitLine(string line, char delimiter)
    {
        if (delimiter == ',')
        {
            // Simple CSV quote handling
            var parts = new List<string>();
            var currentPart = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    parts.Add(currentPart.ToString().Trim('"'));
                    currentPart.Clear();
                }
                else
                {
                    currentPart.Append(c);
                }
            }

            parts.Add(currentPart.ToString().Trim('"'));
            return parts.ToArray();
        }
        else
        {
            return line.Split(delimiter);
        }
    }

    private List<ImportRow> ParseJsonContent(string content, bool hasHeaderRow)
    {
        var rows = new List<ImportRow>();

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                int rowNumber = 0;
                foreach (var item in root.EnumerateArray())
                {
                    rowNumber++;

                    // Skip first item if it's a header indicator
                    if (hasHeaderRow && rowNumber == 1)
                        continue;

                    string? targetTerm = null;
                    string? nativeTerm = null;

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        // JSON object: { "target": "...", "native": "..." }
                        // Try common property names
                        if (item.TryGetProperty("target", out var targetProp))
                            targetTerm = targetProp.GetString();
                        else if (item.TryGetProperty("targetLanguageTerm", out targetProp))
                            targetTerm = targetProp.GetString();
                        else if (item.TryGetProperty("korean", out targetProp))
                            targetTerm = targetProp.GetString();
                        else if (item.TryGetProperty("term", out targetProp))
                            targetTerm = targetProp.GetString();

                        if (item.TryGetProperty("native", out var nativeProp))
                            nativeTerm = nativeProp.GetString();
                        else if (item.TryGetProperty("nativeLanguageTerm", out nativeProp))
                            nativeTerm = nativeProp.GetString();
                        else if (item.TryGetProperty("english", out nativeProp))
                            nativeTerm = nativeProp.GetString();
                        else if (item.TryGetProperty("translation", out nativeProp))
                            nativeTerm = nativeProp.GetString();
                        else if (item.TryGetProperty("definition", out nativeProp))
                            nativeTerm = nativeProp.GetString();
                    }
                    else if (item.ValueKind == JsonValueKind.Array)
                    {
                        // JSON array: ["target", "native"]
                        var arr = item.EnumerateArray().ToArray();
                        if (arr.Length > 0)
                            targetTerm = arr[0].GetString();
                        if (arr.Length > 1)
                            nativeTerm = arr[1].GetString();
                    }

                    var rowStatus = RowStatus.Ok;
                    string? error = null;

                    if (string.IsNullOrEmpty(targetTerm))
                    {
                        rowStatus = RowStatus.Error;
                        error = "Target language term is required.";
                    }
                    else if (string.IsNullOrEmpty(nativeTerm))
                    {
                        rowStatus = RowStatus.Warning;
                        error = "Native language term missing (will use AI translation).";
                    }

                    rows.Add(new ImportRow
                    {
                        RowNumber = rowNumber,
                        TargetLanguageTerm = targetTerm,
                        NativeLanguageTerm = nativeTerm,
                        Status = rowStatus,
                        Error = error,
                        IsSelected = rowStatus != RowStatus.Error
                    });
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse JSON content");
            rows.Add(new ImportRow
            {
                RowNumber = 1,
                Status = RowStatus.Error,
                Error = $"Invalid JSON format: {ex.Message}"
            });
        }

        return rows;
    }

    private async Task<List<ImportRow>> ParseFreeTextContentAsync(string content, string targetLanguage, string nativeLanguage, string? formatHint, CancellationToken ct)
    {
        _logger.LogInformation("Extracting vocabulary from free-form text via AI ({Length} chars)...", content.Length);

        // Load Scriban template
        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("FreeTextToVocab.scriban-txt");
        using var reader = new StreamReader(templateStream);
        var templateContent = await reader.ReadToEndAsync();
        var scribanTemplate = Template.Parse(templateContent);

        // Render prompt
        var prompt = scribanTemplate.Render(new
        {
            source_text = content,
            target_language = targetLanguage,
            native_language = nativeLanguage,
            format_hint = formatHint
        });

        // Call AI
        var response = await _aiService.SendPrompt<FreeTextVocabularyExtractionResponse>(prompt);

        if (response == null || response.Vocabulary == null || !response.Vocabulary.Any())
        {
            _logger.LogWarning("AI returned no vocabulary from free text");
            return new List<ImportRow>
            {
                new ImportRow
                {
                    RowNumber = 1,
                    Status = RowStatus.Warning,
                    Error = "No vocabulary extracted. Check language settings or provide structured data.",
                    IsSelected = false
                }
            };
        }

        // Convert to ImportRows with confidence mapping
        var rows = new List<ImportRow>();
        for (int i = 0; i < response.Vocabulary.Count; i++)
        {
            var item = response.Vocabulary[i];
            var status = item.Confidence.ToLowerInvariant() switch
            {
                "high" => RowStatus.Ok,
                "medium" => RowStatus.Warning,
                "low" => RowStatus.Error,
                _ => RowStatus.Warning
            };

            var error = status != RowStatus.Ok
                ? $"Confidence: {item.Confidence}" + (string.IsNullOrWhiteSpace(item.Notes) ? "" : $" — {item.Notes}")
                : null;

            rows.Add(new ImportRow
            {
                RowNumber = i + 1,
                TargetLanguageTerm = item.TargetLanguageTerm,
                NativeLanguageTerm = item.NativeLanguageTerm,
                Status = status,
                Error = error,
                IsSelected = status != RowStatus.Error, // Auto-deselect low-confidence rows
                IsAiTranslated = true // Free-text extractions are always AI-generated
            });
        }

        _logger.LogInformation("Extracted {Count} vocabulary items from free text", rows.Count);
        return rows;
    }

    private async Task TranslateMissingNativeTermsAsync(List<ImportRow> rows, string targetLanguage, string nativeLanguage, CancellationToken ct)
    {
        var termsToTranslate = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.TargetLanguageTerm))
            .Select(r => r.TargetLanguageTerm!)
            .ToList();

        if (!termsToTranslate.Any())
            return;

        _logger.LogInformation("Translating {Count} terms via AI...", termsToTranslate.Count);

        // Load Scriban template
        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("TranslateMissingNativeTerms.scriban-txt");
        using var reader = new StreamReader(templateStream);
        var templateContent = await reader.ReadToEndAsync();
        var scribanTemplate = Template.Parse(templateContent);

        // Render prompt
        var prompt = scribanTemplate.Render(new
        {
            terms = termsToTranslate,
            target_language = targetLanguage,
            native_language = nativeLanguage
        });

        // Call AI
        var response = await _aiService.SendPrompt<BulkTranslationResponse>(prompt);

        if (response == null || response.Translations == null || !response.Translations.Any())
        {
            _logger.LogWarning("AI returned no translations");
            return;
        }

        // Map translations back to rows
        var translationDict = response.Translations.ToDictionary(t => t.TargetLanguageTerm, t => t.NativeLanguageTerm, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.TargetLanguageTerm) && translationDict.TryGetValue(row.TargetLanguageTerm, out var translation))
            {
                if (translation != "[unknown]")
                {
                    row.NativeLanguageTerm = translation;
                }
                else
                {
                    row.Status = RowStatus.Error;
                    row.Error = "AI could not translate this term.";
                }
            }
        }

        _logger.LogInformation("Translation complete: {Count} terms filled", translationDict.Count(kv => kv.Value != "[unknown]"));
    }

    public ContentTypeDetectionResult DetectContentType(string content, string? formatHint)
    {
        // MVP: Return explicit vocabulary type
        // TODO (Wave 2): Implement AI-based content type classification here
        return new ContentTypeDetectionResult
        {
            ContentType = ContentType.Vocabulary,
            Confidence = 1.0f,
            Note = "Content type detection not yet implemented; defaulting to Vocabulary. Coming in v2."
        };
    }

    public async Task<ContentImportResult> CommitImportAsync(ContentImportCommit commit, CancellationToken ct = default)
    {
        if (commit == null)
            throw new ArgumentNullException(nameof(commit));
        if (commit.Preview == null)
            throw new ArgumentNullException(nameof(commit.Preview));
        if (commit.Target == null)
            throw new ArgumentNullException(nameof(commit.Target));

        ct.ThrowIfCancellationRequested();

        var warnings = new List<string>(commit.Preview.Warnings);
        int createdCount = 0;
        int skippedCount = 0;
        int updatedCount = 0;
        int failedCount = 0;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Step 1: Get or create the target resource
            LearningResource targetResource;

            if (commit.Target.Mode == ImportTargetMode.Existing)
            {
                if (string.IsNullOrEmpty(commit.Target.ExistingResourceId))
                    throw new ArgumentException("ExistingResourceId is required when Mode is Existing.", nameof(commit.Target));

                targetResource = await db.LearningResources
                    .Include(r => r.VocabularyMappings)
                    .FirstOrDefaultAsync(r => r.Id == commit.Target.ExistingResourceId, ct);

                if (targetResource == null)
                    throw new InvalidOperationException($"Resource with ID {commit.Target.ExistingResourceId} not found.");

                _logger.LogInformation("Appending vocabulary to existing resource: {ResourceId} ({Title})",
                    targetResource.Id, targetResource.Title);
            }
            else // ImportTargetMode.New
            {
                if (string.IsNullOrEmpty(commit.Target.NewResourceTitle))
                    throw new ArgumentException("NewResourceTitle is required when Mode is New.", nameof(commit.Target));

                targetResource = new LearningResource
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = commit.Target.NewResourceTitle,
                    Description = commit.Target.NewResourceDescription ?? string.Empty,
                    MediaType = "Vocabulary List",
                    Language = commit.Target.TargetLanguage,
                    Tags = "imported",
                    IsSmartResource = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    VocabularyMappings = new List<ResourceVocabularyMapping>()
                };

                db.LearningResources.Add(targetResource);
                await db.SaveChangesAsync(ct); // Save resource first to get ID

                _logger.LogInformation("Created new resource: {ResourceId} ({Title})", targetResource.Id, targetResource.Title);
            }

            // Step 2: Get existing mappings for this resource to prevent duplicates
            var existingMappingWordIds = new HashSet<string>(
                targetResource.VocabularyMappings.Select(m => m.VocabularyWordId),
                StringComparer.Ordinal);

            // Step 3: Process selected rows with dedup logic
            // CRITICAL: Follow the SaveResourceAsync transaction pattern from LearningResourceRepository
            // - Detach nav props
            // - Dedup check (case-sensitive, trimmed)
            // - Save words first
            // - Create mappings
            // - Single SaveChanges

            var selectedRows = commit.Preview.Rows.Where(r => r.IsSelected && r.Status != RowStatus.Error).ToList();

            foreach (var row in selectedRows)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(row.TargetLanguageTerm))
                {
                    failedCount++;
                    warnings.Add($"Row {row.RowNumber}: Target language term is empty.");
                    continue;
                }

                // TODO (River, Wave 2): Handle single-column imports with AI translation here
                if (string.IsNullOrEmpty(row.NativeLanguageTerm))
                {
                    failedCount++;
                    warnings.Add($"Row {row.RowNumber}: Native language term is empty (AI translation not yet implemented).");
                    continue;
                }

                // Dedup check: case-sensitive, whitespace-trimmed (matches YouTube pipeline + Captain's ruling)
                var trimmedTarget = row.TargetLanguageTerm.Trim();
                var existingWord = await db.VocabularyWords
                    .FirstOrDefaultAsync(w => w.TargetLanguageTerm == trimmedTarget, ct);

                VocabularyWord wordToMap;

                if (existingWord != null)
                {
                    // Word already exists in database
                    switch (commit.DedupMode)
                    {
                        case DedupMode.Skip:
                            // Reuse existing word
                            wordToMap = existingWord;
                            skippedCount++;
                            _logger.LogDebug("Skipping duplicate word: {TargetTerm}", trimmedTarget);
                            break;

                        case DedupMode.Update:
                            // Update existing word with new native term (DANGEROUS: affects all resources using this word)
                            existingWord.NativeLanguageTerm = row.NativeLanguageTerm?.Trim();
                            existingWord.Language = commit.Target.TargetLanguage;
                            existingWord.UpdatedAt = DateTime.UtcNow;

                            // Detach navigation properties to prevent cascade issues
                            if (existingWord.LearningResources?.Any() == true)
                            {
                                foreach (var resource in existingWord.LearningResources)
                                {
                                    db.Entry(resource).State = EntityState.Detached;
                                }
                                existingWord.LearningResources.Clear();
                            }

                            if (existingWord.ResourceMappings?.Any() == true)
                            {
                                foreach (var mapping in existingWord.ResourceMappings)
                                {
                                    db.Entry(mapping).State = EntityState.Detached;
                                }
                                existingWord.ResourceMappings.Clear();
                            }

                            db.VocabularyWords.Update(existingWord);
                            wordToMap = existingWord;
                            updatedCount++;
                            _logger.LogWarning("Updating shared word: {TargetTerm} (affects all resources using this word)", trimmedTarget);
                            break;

                        case DedupMode.ImportAll:
                            // Import as new word even if duplicate (creates duplicate entries)
                            var newDuplicate = new VocabularyWord
                            {
                                Id = Guid.NewGuid().ToString(),
                                TargetLanguageTerm = trimmedTarget,
                                NativeLanguageTerm = row.NativeLanguageTerm?.Trim(),
                                Language = commit.Target.TargetLanguage,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            db.VocabularyWords.Add(newDuplicate);
                            wordToMap = newDuplicate;
                            createdCount++;
                            _logger.LogDebug("Importing duplicate word as new entry: {TargetTerm}", trimmedTarget);
                            break;

                        default:
                            throw new InvalidOperationException($"Unknown DedupMode: {commit.DedupMode}");
                    }
                }
                else
                {
                    // New word — create it
                    var newWord = new VocabularyWord
                    {
                        Id = Guid.NewGuid().ToString(),
                        TargetLanguageTerm = trimmedTarget,
                        NativeLanguageTerm = row.NativeLanguageTerm?.Trim(),
                        Language = commit.Target.TargetLanguage,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    db.VocabularyWords.Add(newWord);
                    wordToMap = newWord;
                    createdCount++;
                    _logger.LogDebug("Creating new word: {TargetTerm}", trimmedTarget);
                }

                // Step 4: Create mapping if it doesn't already exist for this resource
                if (!existingMappingWordIds.Contains(wordToMap.Id))
                {
                    var mapping = new ResourceVocabularyMapping
                    {
                        Id = Guid.NewGuid().ToString(),
                        ResourceId = targetResource.Id,
                        VocabularyWordId = wordToMap.Id
                    };

                    db.ResourceVocabularyMappings.Add(mapping);
                    existingMappingWordIds.Add(wordToMap.Id); // Prevent duplicate mappings within this import
                    _logger.LogDebug("Creating mapping: Resource {ResourceId} -> Word {WordId}", targetResource.Id, wordToMap.Id);
                }
                else
                {
                    _logger.LogDebug("Mapping already exists for word {WordId} in resource {ResourceId}", wordToMap.Id, targetResource.Id);
                }
            }

            // Step 5: Update resource timestamp
            targetResource.UpdatedAt = DateTime.UtcNow;
            db.LearningResources.Update(targetResource);

            // Step 6: Single SaveChanges for the entire transaction
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Import complete: {Created} created, {Skipped} skipped, {Updated} updated, {Failed} failed",
                createdCount, skippedCount, updatedCount, failedCount);

            // Trigger sync (fire-and-forget)
            var syncService = _serviceProvider.GetService<ISyncService>();
            syncService?.TriggerSyncAsync().ConfigureAwait(false);

            return new ContentImportResult
            {
                ResourceId = targetResource.Id,
                CreatedCount = createdCount,
                SkippedCount = skippedCount,
                UpdatedCount = updatedCount,
                FailedCount = failedCount,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during import commit");
            throw;
        }
    }
}

// ===========================
// DTOs
// ===========================

/// <summary>
/// Request to parse content for import preview.
/// </summary>
public class ContentImportRequest
{
    [Description("Raw text content to import (e.g., pasted CSV/TSV)")]
    public string? RawText { get; set; }

    [Description("File bytes for file-based imports")]
    public byte[]? FileBytes { get; set; }

    [Description("Original file name (for format detection hints)")]
    public string? FileName { get; set; }

    [Description("Optional user-provided format description (e.g., 'comma-separated vocabulary list')")]
    public string? FormatHint { get; set; }

    [Description("Content type to import: Vocabulary, Phrases, Transcript, or Auto-detect")]
    public ContentType ContentType { get; set; } = ContentType.Vocabulary;

    [Description("Delimiter character override (e.g., ',' or '\\t')")]
    public char? DelimiterOverride { get; set; }

    [Description("Whether the first row is a header row (skip during parsing)")]
    public bool HasHeaderRow { get; set; }

    [Description("Target language of the content being imported (e.g., 'Korean', 'Spanish')")]
    public string TargetLanguage { get; set; } = "Korean";

    [Description("Native language for translations (e.g., 'English')")]
    public string NativeLanguage { get; set; } = "English";
}

/// <summary>
/// Preview of parsed import rows before commit.
/// </summary>
public class ContentImportPreview
{
    [Description("Parsed rows with validation status")]
    public IReadOnlyList<ImportRow> Rows { get; set; } = Array.Empty<ImportRow>();

    [Description("Detected format description (e.g., 'CSV', 'TSV', 'Free-form text')")]
    public string DetectedFormat { get; set; } = string.Empty;

    [Description("Detected content type with confidence")]
    public ContentTypeDetectionResult DetectedContentType { get; set; } = new();

    [Description("Warnings encountered during parsing")]
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>
/// A single row in the import preview.
/// </summary>
public class ImportRow
{
    [Description("Row number in the source content (1-based)")]
    public int RowNumber { get; set; }

    [Description("Target language term (e.g., Korean word)")]
    public string? TargetLanguageTerm { get; set; }

    [Description("Native language term (e.g., English translation)")]
    public string? NativeLanguageTerm { get; set; }

    [Description("Row validation status: Ok, Warning, or Error")]
    public RowStatus Status { get; set; } = RowStatus.Ok;

    [Description("Validation error or warning message")]
    public string? Error { get; set; }

    [Description("Whether this row is selected for import (user can toggle)")]
    public bool IsSelected { get; set; } = true;

    [Description("Whether the native term was AI-translated (for UI badging)")]
    public bool IsAiTranslated { get; set; }
}

/// <summary>
/// Request to commit the parsed preview to the database.
/// </summary>
public class ContentImportCommit
{
    [Description("The preview to commit (with user edits and selections)")]
    public ContentImportPreview Preview { get; set; } = new();

    [Description("Target resource configuration (new or existing)")]
    public ImportTarget Target { get; set; } = new();

    [Description("Deduplication mode: Skip, Update, or ImportAll")]
    public DedupMode DedupMode { get; set; } = DedupMode.Skip;
}

/// <summary>
/// Target resource for the import (new or existing).
/// </summary>
public class ImportTarget
{
    [Description("Import target mode: New or Existing")]
    public ImportTargetMode Mode { get; set; } = ImportTargetMode.New;

    [Description("Existing resource ID (required if Mode is Existing)")]
    public string? ExistingResourceId { get; set; }

    [Description("New resource title (required if Mode is New)")]
    public string? NewResourceTitle { get; set; }

    [Description("New resource description (optional)")]
    public string? NewResourceDescription { get; set; }

    [Description("Target language for the resource (e.g., 'Korean')")]
    public string TargetLanguage { get; set; } = "Korean";

    [Description("Native language for the resource (e.g., 'English')")]
    public string NativeLanguage { get; set; } = "English";
}

/// <summary>
/// Result of a committed import.
/// </summary>
public class ContentImportResult
{
    [Description("ID of the resource that was created or appended to")]
    public string ResourceId { get; set; } = string.Empty;

    [Description("Number of new vocabulary words created")]
    public int CreatedCount { get; set; }

    [Description("Number of existing words skipped (reused)")]
    public int SkippedCount { get; set; }

    [Description("Number of existing words updated")]
    public int UpdatedCount { get; set; }

    [Description("Number of rows that failed to import")]
    public int FailedCount { get; set; }

    [Description("Warnings encountered during commit")]
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Content type detection result.
/// </summary>
public class ContentTypeDetectionResult
{
    [Description("Detected content type")]
    public ContentType ContentType { get; set; } = ContentType.Vocabulary;

    [Description("Confidence score (0.0 to 1.0)")]
    public float Confidence { get; set; }

    [Description("Additional note about the detection")]
    public string Note { get; set; } = string.Empty;
}

// ===========================
// Enums
// ===========================

/// <summary>
/// Content type for import.
/// </summary>
public enum ContentType
{
    [Description("Vocabulary words with translations")]
    Vocabulary,

    [Description("Phrases or sentence patterns")]
    Phrases,

    [Description("Transcript or free-form text")]
    Transcript,

    [Description("Auto-detect content type")]
    Auto
}

/// <summary>
/// Row validation status.
/// </summary>
public enum RowStatus
{
    [Description("Row is valid and ready to import")]
    Ok,

    [Description("Row has warnings but can be imported")]
    Warning,

    [Description("Row has errors and cannot be imported")]
    Error
}

/// <summary>
/// Import target mode (new or existing resource).
/// </summary>
public enum ImportTargetMode
{
    [Description("Create a new learning resource")]
    New,

    [Description("Append to an existing learning resource")]
    Existing
}

/// <summary>
/// Deduplication mode for vocabulary words.
/// </summary>
public enum DedupMode
{
    [Description("Skip duplicates and reuse existing words (safest, default)")]
    Skip,

    [Description("Update existing words with new native terms (WARNING: affects all resources using the word)")]
    Update,

    [Description("Import all as new entries, even if duplicates exist")]
    ImportAll
}
