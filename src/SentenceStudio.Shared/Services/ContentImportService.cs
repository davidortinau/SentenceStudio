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
/// v1.1: Vocabulary, Phrases, Transcripts, and Auto-detect with checkbox harvest model.
/// </summary>
public interface IContentImportService
{
    /// <summary>
    /// Parse content from text or file input and return a preview of rows to import.
    /// </summary>
    Task<ContentImportPreview> ParseContentAsync(ContentImportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Classify content type using AI. Returns type, confidence, reasoning, and signals.
    /// Confidence tiers: >=0.85 auto-route, 0.70-0.84 suggest, <0.70 user must pick.
    /// </summary>
    Task<ContentClassificationResult> ClassifyContentAsync(string content, string? formatHint, CancellationToken ct = default);

    /// <summary>
    /// Commit the parsed import to the database. Creates or appends to a LearningResource,
    /// creates VocabularyWord rows with dedup, and creates ResourceVocabularyMapping rows.
    /// Respects harvest checkboxes (harvestTranscript, harvestPhrases, harvestWords).
    /// </summary>
    Task<ContentImportResult> CommitImportAsync(ContentImportCommit commit, CancellationToken ct = default);
}

public class ContentImportService : IContentImportService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LearningResourceRepository _resourceRepo;
    private readonly ILogger<ContentImportService> _logger;
    private readonly IAiService _aiService;
    private readonly IFileSystemService _fileSystem;
    private readonly SentenceStudio.Abstractions.IPreferencesService? _preferences;

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
        _preferences = serviceProvider?.GetService<SentenceStudio.Abstractions.IPreferencesService>();
    }

    private string ActiveUserId => _preferences?.Get("active_profile_id", string.Empty) ?? string.Empty;


    public async Task<ContentImportPreview> ParseContentAsync(ContentImportRequest request, CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        ct.ThrowIfCancellationRequested();

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
            content = System.Text.Encoding.UTF8.GetString(request.FileBytes);
        }
        else
        {
            throw new ArgumentException("Either RawText or FileBytes must be provided.", nameof(request));
        }

        // --- Auto-detect branch ---
        // If ContentType is Auto, run classification FIRST before any DB persistence.
        // Confidence gate: >=0.85 auto-route, 0.70-0.84 suggest, <0.70 user picks.
        ContentClassificationResult? classification = null;
        var effectiveContentType = request.ContentType;

        if (request.ContentType == ContentType.Auto)
        {
            classification = await ClassifyContentAsync(content, request.FormatHint, ct);

            if (classification.Confidence >= 0.85f)
            {
                effectiveContentType = classification.ContentType;
                _logger.LogInformation("Auto-detect: high confidence ({Confidence:F2}) → routing to {Type}",
                    classification.Confidence, effectiveContentType);
            }
            else
            {
                // Return classification result to UI for user confirmation — do NOT persist yet.
                return new ContentImportPreview
                {
                    Rows = Array.Empty<ImportRow>(),
                    DetectedFormat = "Pending classification confirmation",
                    DetectedContentType = new ContentTypeDetectionResult
                    {
                        ContentType = classification.ContentType,
                        Confidence = classification.Confidence,
                        Note = classification.Reasoning
                    },
                    Classification = classification,
                    Warnings = classification.Confidence >= 0.70f
                        ? new[] { $"Auto-detect suggests '{classification.ContentType}' ({classification.Confidence:P0} confidence). Please confirm before import." }
                        : new[] { $"Auto-detect is uncertain ({classification.Confidence:P0} confidence). Please select a content type manually." },
                    RequiresUserConfirmation = true,
                    SourceText = content
                };
            }
        }

        // --- Transcript branch ---
        // For Transcript content, we store the full text and extract words (not phrases).
        if (effectiveContentType == ContentType.Transcript)
        {
            // Reject content >30KB (v1.1 limit; chunking deferred to v1.2)
            if (content.Length > 30_000)
            {
                throw new InvalidOperationException(
                    $"Transcript is too large ({content.Length:N0} chars, limit 30,000). " +
                    "Chunking support is planned for v1.2. Please split the transcript into smaller segments.");
            }

            // Extract vocabulary from transcript using the existing prompt
            rows = await ExtractVocabularyFromTranscriptAsync(content, request.TargetLanguage, request.NativeLanguage, ct);

            // Filter based on harvest checkboxes
            if (!request.HarvestWords && !request.HarvestPhrases)
            {
                // User only wants the transcript stored, no vocab extraction
                rows.Clear();
                warnings.Add("Transcript will be stored but no vocabulary extraction was requested.");
            }
            else
            {
                rows = FilterRowsByHarvestFlags(rows, request.HarvestWords, request.HarvestPhrases);
            }

            return new ContentImportPreview
            {
                Rows = rows,
                DetectedFormat = "Transcript",
                DetectedContentType = new ContentTypeDetectionResult
                {
                    ContentType = ContentType.Transcript,
                    Confidence = classification?.Confidence ?? 1.0f,
                    Note = effectiveContentType == request.ContentType ? "Explicitly set by user" : $"Auto-detected ({classification?.Confidence:P0})"
                },
                Classification = classification,
                Warnings = rows.Count == 0 && (request.HarvestWords || request.HarvestPhrases)
                    ? new[] { "No vocabulary extracted from transcript. The transcript will still be stored on the resource." }
                    : warnings,
                SourceText = content
            };
        }

        // --- Phrases / Sentences branch ---
        // Content type is Phrases or Sentences: parse delimited lines as primary entries,
        // then optionally run AI to harvest constituent words/phrases.
        if (effectiveContentType == ContentType.Phrases || effectiveContentType == ContentType.Sentences)
        {
            if (content.Length > 50_000)
            {
                warnings.Add($"Content is too large for phrase extraction ({content.Length} chars). Please split into smaller chunks.");
                rows.Add(new ImportRow
                {
                    RowNumber = 1,
                    Status = RowStatus.Error,
                    Error = "Content exceeds size limit for phrase extraction (50KB)."
                });
            }
            else
            {
                // Step 1: Parse delimited lines to create primary phrase/sentence entries.
                // The user's original content is preserved as-is in each row.
                var (phraseFormatType, phraseDelimiter) = DetectFormat(content, request.DelimiterOverride, request.FormatHint);

                List<ImportRow> primaryRows;
                if (phraseFormatType is "CSV" or "TSV" or "Pipe")
                {
                    primaryRows = ParseDelimitedContent(content, phraseDelimiter!.Value, request.HasHeaderRow);
                    // Reclassify: delimited parser defaults to Word, but these are phrases/sentences.
                    // Use the user's explicit content type as the strongest hint — Captain's directive:
                    // ContentType=Sentences → Sentence, ContentType=Phrases → Phrase (for multi-token).
                    var typeHint = effectiveContentType == ContentType.Sentences
                        ? LexicalUnitType.Sentence
                        : LexicalUnitType.Phrase;
                    foreach (var row in primaryRows)
                    {
                        row.LexicalUnitType = ResolveLexicalUnitType(typeHint, row.TargetLanguageTerm);
                    }
                }
                else
                {
                    // Unstructured input: no delimited primary rows to create.
                    primaryRows = new List<ImportRow>();
                }

                // Step 2: Run AI extraction to harvest constituent words/phrases.
                // Use the Sentences prompt when content type is Sentences (passes harvest
                // flags so the AI knows which entry types to produce); use Phrases prompt otherwise.
                List<ImportRow> aiRows = new();
                if (request.HarvestWords || primaryRows.Count == 0)
                {
                    try
                    {
                        if (effectiveContentType == ContentType.Sentences)
                        {
                            aiRows = await ExtractVocabularyFromSentencesAsync(
                                content, request.TargetLanguage, request.NativeLanguage,
                                request.HarvestSentences, request.HarvestPhrases, request.HarvestWords, ct);
                        }
                        else
                        {
                            aiRows = await ExtractVocabularyFromPhrasesAsync(
                                content, request.TargetLanguage, request.NativeLanguage, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AI extraction failed for {ContentType}", effectiveContentType);
                        warnings.Add($"AI word extraction failed: {ex.Message}. Primary entries are still available.");
                    }
                }

                // Step 3: Combine primary entries + AI-extracted entries, deduplicating by target term.
                var seenTargets = new HashSet<string>(StringComparer.Ordinal);
                rows = new List<ImportRow>();

                foreach (var row in primaryRows)
                {
                    if (!string.IsNullOrWhiteSpace(row.TargetLanguageTerm))
                    {
                        seenTargets.Add(row.TargetLanguageTerm.Trim());
                        rows.Add(row);
                    }
                }

                int rowNum = rows.Count;
                foreach (var row in aiRows)
                {
                    if (!string.IsNullOrWhiteSpace(row.TargetLanguageTerm)
                        && seenTargets.Add(row.TargetLanguageTerm.Trim()))
                    {
                        row.RowNumber = ++rowNum;
                        rows.Add(row);
                    }
                }

                // Step 4: Single-column translation for rows missing native terms
                var phraseSingleColumnRows = rows.Where(r =>
                    string.IsNullOrWhiteSpace(r.NativeLanguageTerm)
                    && !string.IsNullOrWhiteSpace(r.TargetLanguageTerm)).ToList();
                if (phraseSingleColumnRows.Any())
                {
                    try
                    {
                        await TranslateMissingNativeTermsAsync(phraseSingleColumnRows, request.TargetLanguage, request.NativeLanguage, ct);
                        foreach (var row in phraseSingleColumnRows.Where(r => !string.IsNullOrWhiteSpace(r.NativeLanguageTerm)))
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
                        _logger.LogWarning(ex, "AI translation failed for phrase import single-column rows");
                    }
                }

                // Step 5: Filter by harvest flags
                rows = FilterRowsByHarvestFlags(rows, request.HarvestWords, request.HarvestPhrases, request.HarvestSentences);
            }

            var formatLabel = effectiveContentType == ContentType.Sentences
                ? "Sentences (parsed + AI-extracted)"
                : "Phrases (parsed + AI-extracted)";

            return new ContentImportPreview
            {
                Rows = rows,
                DetectedFormat = formatLabel,
                DetectedContentType = new ContentTypeDetectionResult
                {
                    ContentType = effectiveContentType,
                    Confidence = classification?.Confidence ?? 1.0f,
                    Note = effectiveContentType == request.ContentType ? "Explicitly set by user" : $"Auto-detected ({classification?.Confidence:P0})"
                },
                Classification = classification,
                Warnings = warnings,
                SourceText = content
            };
        }

        // --- Vocabulary branch (default) ---
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

        var detectedContentType = new ContentTypeDetectionResult
        {
            ContentType = effectiveContentType,
            Confidence = classification?.Confidence ?? 1.0f,
            Note = request.ContentType == ContentType.Auto
                ? $"Auto-detected as vocabulary ({classification?.Confidence:P0})"
                : "Explicitly set by user"
        };

        return new ContentImportPreview
        {
            Rows = rows,
            DetectedFormat = detectedFormat,
            DetectedContentType = detectedContentType,
            Classification = classification,
            Warnings = warnings,
            SourceText = content
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
                IsSelected = rowStatus != RowStatus.Error, // Auto-deselect error rows
                LexicalUnitType = ResolveLexicalUnitType(LexicalUnitType.Word, targetTerm)
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
                IsAiTranslated = true, // Free-text extractions are always AI-generated
                LexicalUnitType = ResolveLexicalUnitType(item.LexicalUnitType, item.TargetLanguageTerm)
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

    public async Task<ContentClassificationResult> ClassifyContentAsync(string content, string? formatHint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Content cannot be empty.", nameof(content));

        // Cap the content sent to AI for classification (first 5KB is enough for signals)
        var classificationSample = content.Length > 5_000 ? content[..5_000] : content;

        try
        {
            // TODO: Use River's ClassifyImportContent.scriban-txt when it lands.
            // For now, use inline prompt to unblock Phrase/Transcript/Auto pipeline.
            var prompt = BuildClassificationPrompt(classificationSample, formatHint);
            var response = await _aiService.SendPrompt<ContentClassificationAiResponse>(prompt);

            if (response == null)
            {
                _logger.LogWarning("AI classification returned null — defaulting to Vocabulary");
                return new ContentClassificationResult
                {
                    ContentType = ContentType.Vocabulary,
                    Confidence = 0.5f,
                    Reasoning = "AI classification failed; defaulting to Vocabulary.",
                    Signals = new List<string> { "classification_failed" }
                };
            }

            var contentType = response.Type?.ToLowerInvariant() switch
            {
                "vocabulary" => ContentType.Vocabulary,
                "phrases" => ContentType.Phrases,
                "sentences" => ContentType.Sentences,
                "transcript" => ContentType.Transcript,
                _ => ContentType.Vocabulary
            };

            return new ContentClassificationResult
            {
                ContentType = contentType,
                Confidence = Math.Clamp(response.Confidence, 0f, 1f),
                Reasoning = response.Reasoning ?? string.Empty,
                Signals = response.Signals ?? new List<string>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content classification failed");
            return new ContentClassificationResult
            {
                ContentType = ContentType.Vocabulary,
                Confidence = 0.3f,
                Reasoning = $"Classification error: {ex.Message}. Defaulting to Vocabulary.",
                Signals = new List<string> { "error", ex.GetType().Name }
            };
        }
    }

    private static string BuildClassificationPrompt(string sample, string? formatHint)
    {
        var hintSection = formatHint != null ? $"**USER HINT:** {formatHint}\n\n" : "";

        return $"""
            You are a content classifier for a language learning app. Analyze the following text and determine what type of content it is.

            **TEXT SAMPLE:**
            {sample}

            {hintSection}**CLASSIFICATION RULES:**
            1. **Vocabulary** — structured word list, CSV/TSV with target+native columns, or simple word pairs.
            2. **Phrases** — multi-word expressions, idioms, or collocations that are NOT complete sentences. No terminal punctuation or subject+verb structure required.
            3. **Sentences** — complete grammatical sentences (subject + predicate) ending with terminal punctuation (. ! ? 。 ！ ？). Each line is independent with no narrative continuity.
            4. **Transcript** — continuous prose with sentence-to-sentence continuity. Reading as a passage FLOWS naturally. Includes paragraphs, dialogue, or narration.

            **KEY SIGNALS:**
            - Sentence-terminal punctuation (. ! ?) on independent lines → Sentences.
            - Multi-word units WITHOUT terminal punctuation → Phrases.
            - Continuity between sentences (pronouns referencing previous sentences, temporal progression, narrative arc) → Transcript.
            - If each sentence is independent with no contextual link to neighbors, it's Sentences (not Transcript).

            **RESPONSE FORMAT — Return ONLY valid JSON with keys: type, confidence, reasoning, signals.**
            The "type" field must be one of: vocabulary, phrases, sentences, transcript.
            The "confidence" field must be a float between 0.0 and 1.0.
            The "reasoning" field is a brief explanation string.
            The "signals" field is a string array of signal keywords.
            """;
    }

    /// <summary>
    /// Extract vocabulary from transcript text using the ExtractVocabularyFromTranscript prompt.
    /// Word-biased extraction per Captain's D2 refinement.
    /// </summary>
    private async Task<List<ImportRow>> ExtractVocabularyFromTranscriptAsync(
        string transcript, string targetLanguage, string nativeLanguage, CancellationToken ct)
    {
        _logger.LogInformation("Extracting vocabulary from transcript via AI ({Length} chars)...", transcript.Length);

        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("ExtractVocabularyFromTranscript.scriban-txt");
        using var reader = new StreamReader(templateStream);
        var templateContent = await reader.ReadToEndAsync();
        var scribanTemplate = Template.Parse(templateContent);

        var prompt = await scribanTemplate.RenderAsync(new
        {
            native_language = nativeLanguage,
            target_language = targetLanguage,
            video_title = (string?)null,
            channel_name = (string?)null,
            transcript = transcript,
            existing_terms = new List<string>(),
            max_words = 50,
            proficiency_level = (string?)null
        });

        var result = await _aiService.SendPrompt<VocabularyExtractionResponse>(prompt);

        if (result?.Vocabulary == null || !result.Vocabulary.Any())
        {
            _logger.LogWarning("AI returned no vocabulary from transcript");
            return new List<ImportRow>();
        }

        var rows = new List<ImportRow>();
        for (int i = 0; i < result.Vocabulary.Count; i++)
        {
            var item = result.Vocabulary[i];
            rows.Add(new ImportRow
            {
                RowNumber = i + 1,
                TargetLanguageTerm = item.TargetLanguageTerm,
                NativeLanguageTerm = item.NativeLanguageTerm,
                Status = RowStatus.Ok,
                IsSelected = true,
                IsAiTranslated = true,
                LexicalUnitType = ResolveLexicalUnitType(item.LexicalUnitType, item.TargetLanguageTerm)
            });
        }

        _logger.LogInformation("Extracted {Count} vocabulary items from transcript", rows.Count);
        return rows;
    }

    /// <summary>
    /// Extract vocabulary from phrase/sentence content using River's dedicated
    /// ExtractVocabularyFromPhrases prompt. Returns BOTH phrase-level entries AND
    /// constituent word entries as classified by the AI.
    /// </summary>
    private async Task<List<ImportRow>> ExtractVocabularyFromPhrasesAsync(
        string content, string targetLanguage, string nativeLanguage, CancellationToken ct)
    {
        _logger.LogInformation("Extracting vocabulary from phrases via AI ({Length} chars)...", content.Length);

        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("ExtractVocabularyFromPhrases.scriban-txt");
        using var reader = new StreamReader(templateStream);
        var templateContent = await reader.ReadToEndAsync();
        var scribanTemplate = Template.Parse(templateContent);

        var prompt = await scribanTemplate.RenderAsync(new
        {
            native_language = nativeLanguage,
            target_language = targetLanguage,
            source_text = content,
            existing_terms = new List<string>(),
            topik_level = (string?)null
        });

        var result = await _aiService.SendPrompt<FreeTextVocabularyExtractionResponse>(prompt);

        if (result?.Vocabulary == null || !result.Vocabulary.Any())
        {
            _logger.LogWarning("AI returned no vocabulary from phrase extraction");
            return new List<ImportRow>();
        }

        var rows = new List<ImportRow>();
        for (int i = 0; i < result.Vocabulary.Count; i++)
        {
            var item = result.Vocabulary[i];
            var status = item.Confidence.ToLowerInvariant() switch
            {
                "high" => RowStatus.Ok,
                "medium" => RowStatus.Warning,
                "low" => RowStatus.Error,
                _ => RowStatus.Warning
            };

            var error = status != RowStatus.Ok
                ? $"Confidence: {item.Confidence}" + (string.IsNullOrWhiteSpace(item.Notes) ? "" : $" - {item.Notes}")
                : null;

            rows.Add(new ImportRow
            {
                RowNumber = i + 1,
                TargetLanguageTerm = item.TargetLanguageTerm,
                NativeLanguageTerm = item.NativeLanguageTerm,
                Status = status,
                Error = error,
                IsSelected = status != RowStatus.Error,
                IsAiTranslated = true,
                LexicalUnitType = ResolveLexicalUnitType(item.LexicalUnitType, item.TargetLanguageTerm)
            });
        }

        _logger.LogInformation("Extracted {Count} vocabulary items from phrases (phrase + word entries)", rows.Count);
        return rows;
    }

    /// <summary>
    /// Extract vocabulary from sentence content using the Sentences-specific AI prompt.
    /// Passes harvest flags to the Scriban template so the AI knows which entry types to produce.
    /// </summary>
    private async Task<List<ImportRow>> ExtractVocabularyFromSentencesAsync(
        string content, string targetLanguage, string nativeLanguage,
        bool harvestSentences, bool harvestPhrases, bool harvestWords,
        CancellationToken ct)
    {
        _logger.LogInformation("Extracting vocabulary from sentences via AI ({Length} chars)...", content.Length);

        using Stream templateStream = await _fileSystem.OpenAppPackageFileAsync("ExtractVocabularyFromSentences.scriban-txt");
        using var reader = new StreamReader(templateStream);
        var templateContent = await reader.ReadToEndAsync();
        var scribanTemplate = Template.Parse(templateContent);

        var prompt = await scribanTemplate.RenderAsync(new
        {
            native_language = nativeLanguage,
            target_language = targetLanguage,
            source_text = content,
            existing_terms = new List<string>(),
            topik_level = (string?)null,
            harvest_sentences = harvestSentences,
            harvest_phrases = harvestPhrases,
            harvest_words = harvestWords
        });

        var result = await _aiService.SendPrompt<FreeTextVocabularyExtractionResponse>(prompt);

        if (result?.Vocabulary == null || !result.Vocabulary.Any())
        {
            _logger.LogWarning("AI returned no vocabulary from sentence extraction");
            return new List<ImportRow>();
        }

        var rows = new List<ImportRow>();
        for (int i = 0; i < result.Vocabulary.Count; i++)
        {
            var item = result.Vocabulary[i];
            var status = item.Confidence.ToLowerInvariant() switch
            {
                "high" => RowStatus.Ok,
                "medium" => RowStatus.Warning,
                "low" => RowStatus.Error,
                _ => RowStatus.Warning
            };

            var error = status != RowStatus.Ok
                ? $"Confidence: {item.Confidence}" + (string.IsNullOrWhiteSpace(item.Notes) ? "" : $" - {item.Notes}")
                : null;

            rows.Add(new ImportRow
            {
                RowNumber = i + 1,
                TargetLanguageTerm = item.TargetLanguageTerm,
                NativeLanguageTerm = item.NativeLanguageTerm,
                Status = status,
                Error = error,
                IsSelected = status != RowStatus.Error,
                IsAiTranslated = true,
                LexicalUnitType = ResolveLexicalUnitType(item.LexicalUnitType, item.TargetLanguageTerm)
            });
        }

        _logger.LogInformation("Extracted {Count} vocabulary items from sentences (sentence + phrase + word entries)", rows.Count);
        return rows;
    }

    /// <summary>
    /// Filter rows by harvest flags. Removes entries that don't match the requested harvest types.
    /// </summary>
    /// <summary>
    /// Resolve LexicalUnitType with a defensive space-based heuristic fallback.
    /// If the AI/caller classified as Phrase or Sentence AND the term is multi-token,
    /// trust the classification. Single-token terms are always Word regardless of hint.
    /// Captain's directive: user's content type is the strongest signal for multi-token terms.
    /// </summary>
    private static LexicalUnitType ResolveLexicalUnitType(LexicalUnitType aiClassification, string? targetTerm)
    {
        if (string.IsNullOrWhiteSpace(targetTerm))
            return aiClassification == LexicalUnitType.Unknown ? LexicalUnitType.Word : aiClassification;

        var trimmed = targetTerm.Trim();
        var hasWhitespace = trimmed.Contains(' ');

        // Single-token terms are always Word, regardless of caller hint.
        if (!hasWhitespace)
            return LexicalUnitType.Word;

        // Multi-token: trust Phrase/Sentence classification from caller or AI.
        if (aiClassification == LexicalUnitType.Phrase || aiClassification == LexicalUnitType.Sentence)
            return aiClassification;

        // Multi-token term with Word/Unknown hint: use terminal punctuation heuristic.
        var lastChar = trimmed[^1];
        var isSentenceTerminal = lastChar is '.' or '!' or '?' or '。' or '！' or '？';

        if (isSentenceTerminal)
            return LexicalUnitType.Sentence;

        return LexicalUnitType.Phrase;
    }

    private static List<ImportRow> FilterRowsByHarvestFlags(
        List<ImportRow> rows, bool harvestWords, bool harvestPhrases,
        bool harvestSentences = false)
    {
        // Legacy callers pass only words+phrases; treat harvestSentences=false as "include
        // sentences under the harvestPhrases umbrella" for backward compatibility.
        if (harvestWords && harvestPhrases && !harvestSentences)
            return rows; // Harvest both — no filtering needed

        if (harvestWords && harvestPhrases && harvestSentences)
            return rows; // All three — no filtering needed

        return rows.Where(r =>
        {
            var type = r.LexicalUnitType;
            if (harvestWords && (type == LexicalUnitType.Word || type == LexicalUnitType.Unknown))
                return true;
            if (harvestPhrases && type == LexicalUnitType.Phrase)
                return true;
            if (harvestSentences && type == LexicalUnitType.Sentence)
                return true;
            // Backward compat: when HarvestSentences is not explicitly set,
            // Sentence entries ride along with HarvestPhrases.
            if (!harvestSentences && harvestPhrases && type == LexicalUnitType.Sentence)
                return true;
            return false;
        }).ToList();
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

        // Validate harvest checkboxes — at least one must be true
        if (!commit.HarvestTranscript && !commit.HarvestPhrases && !commit.HarvestWords && !commit.HarvestSentences)
        {
            throw new ArgumentException("At least one harvest option must be selected (Transcript, Sentences, Phrases, or Words).", nameof(commit));
        }

        var warnings = new List<string>(commit.Preview.Warnings);
        int createdCount = 0;
        int skippedCount = 0;
        int updatedCount = 0;
        int failedCount = 0;

        // BUG-2 fix: If the UI didn't set TranscriptText explicitly, fall back to
        // the SourceText that round-tripped through the preview.
        var transcriptText = !string.IsNullOrEmpty(commit.TranscriptText)
            ? commit.TranscriptText
            : commit.Preview?.SourceText;

        // BUG-1 fix: Resolve the active user so imported resources are scoped correctly.
        // Matches the pattern in LearningResourceRepository.SaveAsync (ActiveUserId from prefs).
        var userId = ActiveUserId;

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

                // If transcript harvest requested, store/overwrite transcript on existing resource
                if (commit.HarvestTranscript && !string.IsNullOrEmpty(transcriptText))
                {
                    targetResource.Transcript = transcriptText;
                    targetResource.MediaType = "Transcript";
                }

                // Ensure user ownership on existing resource if missing
                if (string.IsNullOrEmpty(targetResource.UserProfileId) && !string.IsNullOrEmpty(userId))
                {
                    targetResource.UserProfileId = userId;
                }

                _logger.LogInformation("Appending vocabulary to existing resource: {ResourceId} ({Title})",
                    targetResource.Id, targetResource.Title);
            }
            else // ImportTargetMode.New
            {
                if (string.IsNullOrEmpty(commit.Target.NewResourceTitle))
                    throw new ArgumentException("NewResourceTitle is required when Mode is New.", nameof(commit.Target));

                // Determine MediaType based on harvest checkboxes
                var mediaType = commit.HarvestTranscript ? "Transcript" : "Vocabulary List";

                targetResource = new LearningResource
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = commit.Target.NewResourceTitle,
                    Description = commit.Target.NewResourceDescription ?? string.Empty,
                    MediaType = mediaType,
                    Transcript = commit.HarvestTranscript ? transcriptText : null,
                    Language = commit.Target.TargetLanguage,
                    Tags = "imported",
                    IsSmartResource = false,
                    UserProfileId = !string.IsNullOrEmpty(userId) ? userId : null,
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

            // In-batch dedup cache: tracks words created/reused during THIS commit so that
            // subsequent rows with the same trimmed target term reuse the same VocabularyWord
            // instead of querying the DB (which can't see tracked-but-unsaved entities) and
            // accidentally creating duplicates within a single import. Honors DedupMode:
            // ImportAll bypasses this cache so it can intentionally create duplicates.
            var batchWordsByTarget = new Dictionary<string, VocabularyWord>(StringComparer.Ordinal);

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

                VocabularyWord wordToMap;

                // First, check the in-batch cache to avoid creating duplicates for same-term rows
                // within this single import. ImportAll mode skips the cache so duplicates can be
                // created intentionally (matching DB query bypass behavior).
                if (commit.DedupMode != DedupMode.ImportAll &&
                    batchWordsByTarget.TryGetValue(trimmedTarget, out var batchWord))
                {
                    wordToMap = batchWord;

                    if (commit.DedupMode == DedupMode.Update)
                    {
                        // Apply the latest non-empty native term so the final saved value reflects
                        // the most recent row's translation, mirroring the "last write wins" semantics
                        // a user would expect from successive Update rows for the same term.
                        var trimmedNative = row.NativeLanguageTerm?.Trim();
                        if (!string.IsNullOrEmpty(trimmedNative))
                        {
                            wordToMap.NativeLanguageTerm = trimmedNative;
                            wordToMap.UpdatedAt = DateTime.UtcNow;
                        }
                    }

                    skippedCount++;
                    _logger.LogDebug("In-batch duplicate reused for term: {TargetTerm}", trimmedTarget);
                }
                else
                {
                    var existingWord = await db.VocabularyWords
                        .FirstOrDefaultAsync(w => w.TargetLanguageTerm == trimmedTarget, ct);

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
                                LexicalUnitType = row.LexicalUnitType,
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
                        LexicalUnitType = row.LexicalUnitType,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    db.VocabularyWords.Add(newWord);
                    wordToMap = newWord;
                    createdCount++;
                    _logger.LogDebug("Creating new word: {TargetTerm}", trimmedTarget);
                }

                    // Cache for the rest of this batch (skip ImportAll so duplicates remain intentional).
                    if (commit.DedupMode != DedupMode.ImportAll)
                    {
                        batchWordsByTarget[trimmedTarget] = wordToMap;
                    }
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

            // Zero-vocab edge case: if transcript was stored but no vocab extracted,
            // persist the resource with a clear status message rather than failing silently.
            if (createdCount == 0 && skippedCount == 0 && updatedCount == 0 && commit.HarvestTranscript)
            {
                warnings.Add("Transcript stored on resource, but no vocabulary was extracted or committed.");
            }
            else if (createdCount == 0 && skippedCount == 0 && updatedCount == 0)
            {
                warnings.Add("No vocabulary entries were imported. Check content format or harvest settings.");
            }

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

    // --- v1.1 checkbox harvest model ---

    [Description("Store full text on LearningResource.Transcript (MediaType='Transcript')")]
    public bool HarvestTranscript { get; set; }

    [Description("Extract Phrase-type entries (LexicalUnitType=Phrase)")]
    public bool HarvestPhrases { get; set; }

    [Description("Extract Word-type entries (LexicalUnitType=Word)")]
    public bool HarvestWords { get; set; } = true;

    [Description("Extract Sentence-type entries (LexicalUnitType=Sentence)")]
    public bool HarvestSentences { get; set; }
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

    [Description("Full AI classification result (null if not auto-detected)")]
    public ContentClassificationResult? Classification { get; set; }

    [Description("True if auto-detect confidence is below threshold and user must confirm")]
    public bool RequiresUserConfirmation { get; set; }

    [Description("Warnings encountered during parsing")]
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

    [Description("Original source text carried through for transcript storage on commit")]
    public string? SourceText { get; set; }
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

    [Description("Lexical unit classification for this entry")]
    public LexicalUnitType LexicalUnitType { get; set; } = LexicalUnitType.Word;
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

    [Description("Store the full text as a transcript on the LearningResource")]
    public bool HarvestTranscript { get; set; }

    [Description("Extract phrase-level entries (LexicalUnitType=Phrase)")]
    public bool HarvestPhrases { get; set; }

    [Description("Extract word-level entries (LexicalUnitType=Word)")]
    public bool HarvestWords { get; set; } = true;

    [Description("Extract sentence-level entries (LexicalUnitType=Sentence)")]
    public bool HarvestSentences { get; set; }

    [Description("Raw transcript text to store on the resource (when HarvestTranscript is true)")]
    public string? TranscriptText { get; set; }
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

    [Description("Complete sentences with translations")]
    Sentences,

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

/// <summary>
/// Result from AI content classification (auto-detect).
/// </summary>
public class ContentClassificationResult
{
    [Description("Classified content type")]
    public ContentType ContentType { get; set; } = ContentType.Vocabulary;

    [Description("Confidence score (0.0 to 1.0). >=0.85 auto-route, 0.70-0.84 suggest, <0.70 user picks.")]
    public float Confidence { get; set; }

    [Description("AI reasoning for the classification")]
    public string Reasoning { get; set; } = string.Empty;

    [Description("Signals the classifier identified (e.g., 'continuity', 'csv_structure', 'standalone_sentences')")]
    public List<string> Signals { get; set; } = new();
}

/// <summary>
/// Internal AI response DTO for content classification prompt.
/// </summary>
internal class ContentClassificationAiResponse
{
    [Description("Classified type: vocabulary, phrases, or transcript")]
    public string? Type { get; set; }

    [Description("Confidence score 0.0-1.0")]
    public float Confidence { get; set; }

    [Description("Brief explanation of the classification")]
    public string? Reasoning { get; set; }

    [Description("Signal keywords that influenced the classification")]
    public List<string>? Signals { get; set; }
}
