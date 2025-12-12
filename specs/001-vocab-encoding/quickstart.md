# Quickstart: Vocabulary Encoding Features

**Feature**: 001-vocab-encoding  
**Audience**: Developers implementing vocabulary encoding features  
**Prerequisites**: Familiarity with .NET MAUI, Entity Framework Core, MauiReactor

## Overview

This guide helps you implement vocabulary encoding features: lemmas, tags, mnemonics, images, and example sentences. **Critical focus**: SQLite performance optimization for mobile devices.

## Development Workflow

### Phase 1: Database Schema (User Story 1 Foundation)

1. **Create Migrations**:
   ```bash
   cd src/SentenceStudio.Shared
   dotnet ef migrations add AddVocabularyEncodingFields --context ApplicationDbContext
   dotnet ef migrations add CreateExampleSentenceTable --context ApplicationDbContext
   ```

2. **Update ApplicationDbContext**:
   ```csharp
   // Add DbSet for ExampleSentence
   public DbSet<ExampleSentence> ExampleSentences { get; set; }
   
   // Configure relationships in OnModelCreating
   protected override void OnModelCreating(ModelBuilder modelBuilder)
   {
       modelBuilder.Entity<ExampleSentence>().ToTable("ExampleSentence").HasKey(e => e.Id);
       
       modelBuilder.Entity<ExampleSentence>()
           .HasOne(es => es.VocabularyWord)
           .WithMany(vw => vw.ExampleSentences)
           .HasForeignKey(es => es.VocabularyWordId)
           .OnDelete(DeleteBehavior.Cascade);
   }
   ```

3. **Test Migration**:
   ```bash
   dotnet build -f net10.0-maccatalyst
   # Run app and verify database schema updated
   ```

### Phase 2: Repositories with Performance Optimization

1. **Implement VocabularyEncodingRepository**:
   ```csharp
   public class VocabularyEncodingRepository : IVocabularyEncodingRepository
   {
       // CRITICAL: Use compiled query for tag filtering (hot path)
       private static readonly Func<ApplicationDbContext, string, IAsyncEnumerable<VocabularyWord>>
           _filterByTagCompiled = EF.CompileAsyncQuery(
               (ApplicationDbContext db, string tag) =>
                   db.VocabularyWords
                       .Where(w => EF.Functions.Like(w.Tags, $"%{tag}%"))
                       .OrderBy(w => w.TargetLanguageTerm));
       
       public async Task<List<VocabularyWord>> FilterByTagAsync(string tag, int pageNumber = 1, int pageSize = 50)
       {
           using var scope = _serviceProvider.CreateScope();
           var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
           
           return await _filterByTagCompiled(db, tag)
               .Skip((pageNumber - 1) * pageSize)
               .Take(pageSize)
               .ToListAsync();
       }
   }
   ```

2. **Implement ExampleSentenceRepository**:
   ```csharp
   public class ExampleSentenceRepository : IExampleSentenceRepository
   {
       // CRITICAL: Batch load counts to avoid N+1
       public async Task<Dictionary<int, int>> GetCountsByVocabularyWordIdsAsync(List<int> vocabularyWordIds)
       {
           using var scope = _serviceProvider.CreateScope();
           var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
           
           return await db.ExampleSentences
               .Where(es => vocabularyWordIds.Contains(es.VocabularyWordId))
               .GroupBy(es => es.VocabularyWordId)
               .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
               .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);
       }
   }
   ```

3. **Register Services**:
   ```csharp
   // In MauiProgram.cs
   builder.Services.AddSingleton<IVocabularyEncodingRepository, VocabularyEncodingRepository>();
   builder.Services.AddSingleton<IExampleSentenceRepository, ExampleSentenceRepository>();
   builder.Services.AddSingleton<IEncodingStrengthCalculator, EncodingStrengthCalculator>();
   ```

### Phase 3: UI Components (User Story 1 - Memory Aids)

**CRITICAL**: Extend existing EditVocabularyWordPage.cs to add encoding metadata fields in the `RenderWordForm()` section.

1. **Add Encoding Fields to EditVocabularyWordPageState**:
   ```csharp
   class EditVocabularyWordPageState
   {
       // Existing fields...
       public string TargetLanguageTerm { get; set; } = string.Empty;
       public string NativeLanguageTerm { get; set; } = string.Empty;
       
       // NEW: Encoding metadata fields
       public string Lemma { get; set; } = string.Empty;
       public string Tags { get; set; } = string.Empty;
       public string MnemonicText { get; set; } = string.Empty;
       public string MnemonicImageUri { get; set; } = string.Empty;
       
       // NEW: Example sentences
       public List<ExampleSentence> ExampleSentences { get; set; } = new();
       public bool IsEditingSentence { get; set; } = false;
       public ExampleSentence EditingSentence { get; set; } = new();
   }
   ```

2. **Extend RenderWordForm() in EditVocabularyWordPage**:
   ```csharp
   VisualNode RenderWordForm() =>
       VStack(spacing: 16,
           Label($"{_localize["VocabularyTerms"]}")
               .FontSize(20)
               .FontAttributes(FontAttributes.Bold),

           // Existing: Target Language field
           RenderTargetLanguageField(),
           
           // Existing: Native Language field
           RenderNativeLanguageField(),
           
           // NEW: Lemma field (dictionary form)
           VStack(spacing: 8,
               Label($"{_localize["VocabLemmaLabel"]}")
                   .FontSize(14)
                   .FontAttributes(FontAttributes.Bold),
               Border(
                   Entry()
                       .Text(State.Lemma)
                       .OnTextChanged(text => SetState(s => s.Lemma = text))
                       .Placeholder($"{_localize["VocabLemmaPlaceholder"]}")
                       .FontSize(16)
               )
               .ThemeKey(MyTheme.InputWrapper)
               .Padding(MyTheme.CardPadding)
           ),
           
           // NEW: Tags field (comma-separated)
           VStack(spacing: 8,
               Label($"{_localize["VocabTagsLabel"]}")
                   .FontSize(14)
                   .FontAttributes(FontAttributes.Bold),
               Border(
                   Entry()
                       .Text(State.Tags)
                       .OnTextChanged(text => SetState(s => s.Tags = text))
                       .Placeholder($"{_localize["VocabTagsPlaceholder"]}")
                       .FontSize(16)
               )
               .ThemeKey(MyTheme.InputWrapper)
               .Padding(MyTheme.CardPadding)
           ),
           
           // NEW: Mnemonic text field (memory aid)
           VStack(spacing: 8,
               Label($"{_localize["VocabMnemonicLabel"]}")
                   .FontSize(14)
                   .FontAttributes(FontAttributes.Bold),
               Border(
                   Editor()
                       .Text(State.MnemonicText)
                       .OnTextChanged(text => SetState(s => s.MnemonicText = text))
                       .Placeholder($"{_localize["VocabMnemonicPlaceholder"]}")
                       .FontSize(16)
                       .HeightRequest(100)
               )
               .ThemeKey(MyTheme.InputWrapper)
               .Padding(MyTheme.CardPadding)
           ),
           
           // NEW: Mnemonic image URL field
           VStack(spacing: 8,
               Label($"{_localize["VocabImageUrlLabel"]}")
                   .FontSize(14)
                   .FontAttributes(FontAttributes.Bold),
               Border(
                   Entry()
                       .Text(State.MnemonicImageUri)
                       .OnTextChanged(text => SetState(s => s.MnemonicImageUri = text))
                       .Placeholder($"{_localize["VocabImageUrlPlaceholder"]}")
                       .Keyboard(Keyboard.Url)
                       .FontSize(16)
               )
               .ThemeKey(MyTheme.InputWrapper)
               .Padding(MyTheme.CardPadding)
           ),
           
           // NEW: Show mnemonic image preview if URL provided
           !string.IsNullOrWhiteSpace(State.MnemonicImageUri) ?
               Image()
                   .Source(State.MnemonicImageUri)
                   .HeightRequest(200)
                   .Aspect(Aspect.AspectFit)
                   .Margin(0, 8, 0, 0) :
               null
       );
   ```

3. **Add New Section: RenderExampleSentences() in EditVocabularyWordPage**:
   ```csharp
   // Add this to the main Render() method's VStack after RenderResourceAssociations()
   VisualNode RenderExampleSentences() =>
       VStack(spacing: 16,
           Label($"{_localize["ExampleSentences"]}")
               .FontSize(20)
               .FontAttributes(FontAttributes.Bold),
           
           // List existing example sentences
           State.ExampleSentences.Count > 0 ?
               VStack(spacing: 8,
                   [.. State.ExampleSentences.Select(sentence =>
                       Border(
                           VStack(spacing: 8,
                               // Target sentence
                               Label(sentence.TargetSentence)
                                   .FontSize(16)
                                   .FontAttributes(sentence.IsCore ? FontAttributes.Bold : FontAttributes.None),
                               
                               // Native translation
                               !string.IsNullOrWhiteSpace(sentence.NativeSentence) ?
                                   Label(sentence.NativeSentence)
                                       .FontSize(14)
                                       .TextColor(MyTheme.SecondaryText) :
                                   null,
                               
                               // Actions: Play audio, Mark as core, Edit, Delete
                               HStack(spacing: 8,
                                   // Play audio button
                                   !string.IsNullOrWhiteSpace(sentence.AudioUri) ?
                                       ImageButton()
                                           .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconPlay)
                                           .HeightRequest(32)
                                           .WidthRequest(32)
                                           .OnClicked(() => PlaySentenceAudioAsync(sentence)) :
                                       Button($"{_localize["GenerateAudio"]}")
                                           .FontSize(12)
                                           .OnClicked(() => GenerateSentenceAudioAsync(sentence)),
                                   
                                   // Core example indicator
                                   sentence.IsCore ?
                                       Border(
                                           Label("⭐ Core")
                                               .FontSize(12)
                                               .Padding(4, 2)
                                       )
                                       .BackgroundColor(MyTheme.SuccessColor)
                                       .StrokeShape(new RoundRectangle().CornerRadius(4)) :
                                       Button($"{_localize["MarkAsCore"]}")
                                           .FontSize(12)
                                           .OnClicked(() => ToggleCoreSentenceAsync(sentence)),
                                   
                                   // Edit button
                                   ImageButton()
                                       .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconEdit)
                                       .HeightRequest(32)
                                       .WidthRequest(32)
                                       .OnClicked(() => EditSentence(sentence)),
                                   
                                   // Delete button
                                   ImageButton()
                                       .Set(Microsoft.Maui.Controls.ImageButton.SourceProperty, MyTheme.IconDelete)
                                       .HeightRequest(32)
                                       .WidthRequest(32)
                                       .OnClicked(() => DeleteSentenceAsync(sentence))
                               )
                           ).Padding(12)
                       )
                       .ThemeKey(MyTheme.CardStyle)
                       .Margin(0, 0, 0, 8)
                   )]
               ) :
               Label($"{_localize["NoExampleSentences"]}")
                   .FontSize(14)
                   .TextColor(MyTheme.SecondaryText)
                   .Margin(0, 8),
           
           // Add/Edit example sentence form
           State.IsEditingSentence ?
               Border(
                   VStack(spacing: 12,
                       Label($"{_localize["AddExampleSentence"]}")
                           .FontSize(16)
                           .FontAttributes(FontAttributes.Bold),
                       
                       // Target sentence input
                       Entry()
                           .Text(State.EditingSentence.TargetSentence)
                           .OnTextChanged(text => SetState(s => s.EditingSentence.TargetSentence = text))
                           .Placeholder($"{_localize["TargetSentencePlaceholder"]}")
                           .FontSize(16),
                       
                       // Native translation input
                       Entry()
                           .Text(State.EditingSentence.NativeSentence)
                           .OnTextChanged(text => SetState(s => s.EditingSentence.NativeSentence = text))
                           .Placeholder($"{_localize["NativeSentencePlaceholder"]}")
                           .FontSize(16),
                       
                       // Core example checkbox
                       HStack(spacing: 8,
                           CheckBox()
                               .IsChecked(State.EditingSentence.IsCore)
                               .OnCheckedChanged((s, e) => SetState(s => s.EditingSentence.IsCore = e.Value)),
                           Label($"{_localize["MarkAsCoreExample"]}")
                               .FontSize(14)
                       ),
                       
                       // Action buttons
                       HStack(spacing: 8,
                           Button($"{_localize["Save"]}")
                               .ThemeKey(MyTheme.Primary)
                               .OnClicked(SaveExampleSentenceAsync),
                           Button($"{_localize["Cancel"]}")
                               .ThemeKey(MyTheme.Secondary)
                               .OnClicked(() => SetState(s => s.IsEditingSentence = false))
                       )
                   ).Padding(12)
               )
               .ThemeKey(MyTheme.CardStyle) :
               Button($"{_localize["AddExampleSentence"]}")
                   .ThemeKey(MyTheme.Secondary)
                   .OnClicked(() => SetState(s =>
                   {
                       s.IsEditingSentence = true;
                       s.EditingSentence = new ExampleSentence
                       {
                           VocabularyWordId = Props.VocabularyWordId
                       };
                   }))
       );
   ```

4. **Add Encoding Strength Indicator**:
   ```csharp
   // Add this helper method
   VisualNode RenderEncodingStrength()
   {
       if (State.Word.Id == 0) return null; // Only for saved words
       
       var score = _encodingCalculator.Calculate(State.Word, State.ExampleSentences.Count);
       var label = _encodingCalculator.GetLabel(score);
       
       var badgeColor = label switch
       {
           "Basic" => MyTheme.WarningColor,
           "Good" => MyTheme.InfoColor,
           "Strong" => MyTheme.SuccessColor,
           _ => MyTheme.SecondaryText
       };
       
       return HStack(spacing: 8,
           Label($"{_localize["EncodingStrength"]}: ")
               .FontSize(14)
               .FontAttributes(FontAttributes.Bold),
           Border(
               Label($"{_localize[$"EncodingStrength{label}"]}")
                   .FontSize(14)
                   .TextColor(Colors.White)
                   .Padding(8, 4)
           )
           .BackgroundColor(badgeColor)
           .StrokeShape(new RoundRectangle().CornerRadius(4))
       ).Margin(0, 16, 0, 0);
   }
   
   // Add RenderEncodingStrength() to the VStack in RenderWordForm()
   ```

5. **Update SaveWord() to Persist Encoding Metadata**:
   ```csharp
   async Task SaveWord()
   {
       SetState(s => s.IsSaving = true);
       
       try
       {
           // Update word with encoding metadata
           State.Word.TargetLanguageTerm = State.TargetLanguageTerm;
           State.Word.NativeLanguageTerm = State.NativeLanguageTerm;
           State.Word.Lemma = State.Lemma;
           State.Word.Tags = State.Tags;
           State.Word.MnemonicText = State.MnemonicText;
           State.Word.MnemonicImageUri = State.MnemonicImageUri;
           State.Word.UpdatedAt = DateTime.Now;
           
           if (State.Word.Id == 0)
           {
               State.Word.CreatedAt = DateTime.Now;
           }
           
           // Save via repository (existing pattern)
           await _vocabularyRepo.SaveAsync(State.Word);
           
           _logger.LogInformation("✅ Saved vocabulary word with encoding metadata: {Word}", State.TargetLanguageTerm);
           
           await MauiControls.Shell.Current.GoToAsync("..");
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error saving vocabulary word");
           SetState(s => s.ErrorMessage = $"{_localize["ErrorSavingWord"]}: {ex.Message}");
       }
       finally
       {
           SetState(s => s.IsSaving = false);
       }
   }
   ```

6. **Add Example Sentence Methods**:
   ```csharp
   [Inject] IExampleSentenceRepository _exampleSentenceRepo;
   [Inject] IEncodingStrengthCalculator _encodingCalculator;
   
   async Task SaveExampleSentenceAsync()
   {
       try
       {
           if (string.IsNullOrWhiteSpace(State.EditingSentence.TargetSentence))
           {
               SetState(s => s.ErrorMessage = $"{_localize["TargetSentenceRequired"]}");
               return;
           }
           
           State.EditingSentence.UpdatedAt = DateTime.Now;
           
           if (State.EditingSentence.Id == 0)
           {
               State.EditingSentence.VocabularyWordId = Props.VocabularyWordId;
               State.EditingSentence.CreatedAt = DateTime.Now;
               var saved = await _exampleSentenceRepo.CreateAsync(State.EditingSentence);
               State.ExampleSentences.Add(saved);
           }
           else
           {
               await _exampleSentenceRepo.UpdateAsync(State.EditingSentence);
               var index = State.ExampleSentences.FindIndex(s => s.Id == State.EditingSentence.Id);
               if (index >= 0)
                   State.ExampleSentences[index] = State.EditingSentence;
           }
           
           SetState(s => s.IsEditingSentence = false);
           _logger.LogInformation("✅ Saved example sentence for word {WordId}", Props.VocabularyWordId);
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error saving example sentence");
           SetState(s => s.ErrorMessage = $"{_localize["ErrorSavingSentence"]}: {ex.Message}");
       }
   }
   
   async Task DeleteSentenceAsync(ExampleSentence sentence)
   {
       try
       {
           await _exampleSentenceRepo.DeleteAsync(sentence.Id);
           State.ExampleSentences.Remove(sentence);
           SetState(s => s); // Trigger re-render
           _logger.LogInformation("✅ Deleted example sentence {SentenceId}", sentence.Id);
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error deleting example sentence");
       }
   }
   
   async Task ToggleCoreSentenceAsync(ExampleSentence sentence)
   {
       try
       {
           var updated = await _exampleSentenceRepo.SetCoreAsync(sentence.Id, !sentence.IsCore);
           var index = State.ExampleSentences.FindIndex(s => s.Id == sentence.Id);
           if (index >= 0)
               State.ExampleSentences[index] = updated;
           SetState(s => s);
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error toggling core example");
       }
   }
   
   async Task GenerateSentenceAudioAsync(ExampleSentence sentence)
   {
       try
       {
           SetState(s => s.IsGeneratingAudio = true);
           
           var audioStream = await _speechService.TextToSpeechAsync(
               sentence.TargetSentence,
               Voices.JiYoung
           );
           
           // Save audio to disk
           var fileName = $"sentence_{sentence.Id}_{DateTime.Now:yyyyMMddHHmmss}.mp3";
           var audioCacheDir = Path.Combine(FileSystem.AppDataDirectory, "AudioCache");
           var filePath = Path.Combine(audioCacheDir, fileName);
           
           if (!Directory.Exists(audioCacheDir))
               Directory.CreateDirectory(audioCacheDir);
           
           using (var fileStream = File.Create(filePath))
           {
               audioStream.Position = 0;
               await audioStream.CopyToAsync(fileStream);
           }
           
           // Update sentence with audio URI
           sentence.AudioUri = filePath;
           await _exampleSentenceRepo.UpdateAsync(sentence);
           
           SetState(s => s);
           _logger.LogInformation("✅ Generated audio for sentence {SentenceId}", sentence.Id);
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error generating sentence audio");
       }
       finally
       {
           SetState(s => s.IsGeneratingAudio = false);
       }
   }
   
   async Task PlaySentenceAudioAsync(ExampleSentence sentence)
   {
       try
       {
           if (string.IsNullOrWhiteSpace(sentence.AudioUri) || !File.Exists(sentence.AudioUri))
               return;
           
           _audioPlayer = AudioManager.Current.CreatePlayer(File.OpenRead(sentence.AudioUri));
           _audioPlayer.Play();
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error playing sentence audio");
       }
   }
   
   void EditSentence(ExampleSentence sentence)
   {
       SetState(s =>
       {
           s.IsEditingSentence = true;
           s.EditingSentence = new ExampleSentence
           {
               Id = sentence.Id,
               VocabularyWordId = sentence.VocabularyWordId,
               TargetSentence = sentence.TargetSentence,
               NativeSentence = sentence.NativeSentence,
               AudioUri = sentence.AudioUri,
               IsCore = sentence.IsCore
           };
       });
   }
   ```

7. **Update LoadData() to Load Example Sentences**:
   ```csharp
   async Task LoadData()
   {
       SetState(s => s.IsLoading = true);
       
       try
       {
           // Existing loading logic...
           
           if (Props.VocabularyWordId > 0)
           {
               // Load word
               State.Word = await _vocabularyRepo.GetByIdAsync(Props.VocabularyWordId);
               
               // Load encoding metadata into form fields
               SetState(s =>
               {
                   s.TargetLanguageTerm = State.Word.TargetLanguageTerm ?? "";
                   s.NativeLanguageTerm = State.Word.NativeLanguageTerm ?? "";
                   s.Lemma = State.Word.Lemma ?? "";
                   s.Tags = State.Word.Tags ?? "";
                   s.MnemonicText = State.Word.MnemonicText ?? "";
                   s.MnemonicImageUri = State.Word.MnemonicImageUri ?? "";
               });
               
               // NEW: Load example sentences
               State.ExampleSentences = await _exampleSentenceRepo.GetByVocabularyWordIdAsync(Props.VocabularyWordId);
           }
           
           // Existing: Load resources, progress, etc.
       }
       catch (Exception ex)
       {
           _logger.LogError(ex, "❌ Error loading vocabulary word data");
       }
       finally
       {
           SetState(s => s.IsLoading = false);
       }
   }
   ```

2. **Add Encoding Strength Indicator**:
   ```csharp
   VisualNode RenderEncodingStrength(VocabularyWord word, int exampleSentenceCount)
   {
       var score = _encodingCalculator.Calculate(word, exampleSentenceCount);
       var label = _encodingCalculator.GetLabel(score);
       
       var badgeColor = label switch
       {
           "Basic" => MyTheme.WarningColor,
           "Good" => MyTheme.InfoColor,
           "Strong" => MyTheme.SuccessColor,
           _ => MyTheme.SecondaryText
       };
       
       return HStack(spacing: MyTheme.Spacing40,
           Label($"{_localize["EncodingStrengthLabel"]}: ").ThemeKey(MyTheme.Body2),
           Border(
               Label($"{_localize[$"EncodingStrength{label}"]}").Padding(MyTheme.Spacing40)
           )
           .BackgroundColor(badgeColor)
           .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.CornerRadius40))
       );
   }
   ```

3. **Render Tag Badges**:
   ```csharp
   VisualNode RenderTagBadges(string? tags)
   {
       if (string.IsNullOrWhiteSpace(tags))
           return null;
       
       var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
       
       return HStack(spacing: MyTheme.Spacing40,
           [.. tagList.Select(tag =>
               Border(
                   Label(tag)
                       .ThemeKey(MyTheme.Caption1)
                       .Padding(MyTheme.Spacing40, MyTheme.Spacing20)
               )
               .BackgroundColor(MyTheme.TagBackgroundColor)
               .StrokeShape(new RoundRectangle().CornerRadius(MyTheme.CornerRadius40))
               .OnTapped(() => OnTagClicked(tag))
           )]
       );
   }
   ```

### Phase 4: Example Sentences (User Story 2)

1. **Create ExampleSentenceEditor Component**:
   ```csharp
   public class ExampleSentenceEditor : Component<ExampleSentenceEditorState>
   {
       [Param] public int VocabularyWordId { get; set; }
       [Param] public Action OnSentenceAdded { get; set; }
       
       public override VisualNode Render()
       {
           return Border(
               VStack(spacing: MyTheme.Spacing120,
                   Label($"{_localize["AddExampleSentenceTitle"]}").ThemeKey(MyTheme.Title2),
                   
                   // Target sentence input
                   Border(
                       Entry()
                           .Text(State.TargetSentence)
                           .Placeholder($"{_localize["TargetSentencePlaceholder"]}")
                           .OnTextChanged(text => SetState(s => s.TargetSentence = text))
                   ).ThemeKey(MyTheme.InputWrapper),
                   
                   // Native translation input
                   Border(
                       Entry()
                           .Text(State.NativeSentence)
                           .Placeholder($"{_localize["NativeSentencePlaceholder"]}")
                           .OnTextChanged(text => SetState(s => s.NativeSentence = text))
                   ).ThemeKey(MyTheme.InputWrapper),
                   
                   // Core example checkbox
                   HStack(spacing: MyTheme.Spacing40,
                       CheckBox()
                           .IsChecked(State.IsCore)
                           .OnCheckedChanged((s, e) => SetState(s => s.IsCore = e.Value)),
                       Label($"{_localize["MarkAsCoreExample"]}").ThemeKey(MyTheme.Body2)
                   ),
                   
                   // Save button
                   Button($"{_localize["SaveSentence"]}")
                       .ThemeKey(MyTheme.Primary)
                       .OnClicked(SaveSentenceAsync)
               ).Padding(MyTheme.Spacing160)
           ).ThemeKey(MyTheme.CardStyle);
       }
       
       async Task SaveSentenceAsync()
       {
           var sentence = new ExampleSentence
           {
               VocabularyWordId = VocabularyWordId,
               TargetSentence = State.TargetSentence,
               NativeSentence = State.NativeSentence,
               IsCore = State.IsCore,
               CreatedAt = DateTime.Now,
               UpdatedAt = DateTime.Now
           };
           
           await _exampleSentenceRepository.CreateAsync(sentence);
           OnSentenceAdded?.Invoke();
       }
   }
   ```

### Phase 5: Filtering and Sorting (User Story 3)

1. **Add Tag Filter to VocabularyListPage**:
   ```csharp
   public class VocabularyListViewModel
   {
       public async Task LoadWordsWithEncodingAsync(string? tagFilter = null, bool sortByEncoding = false)
       {
           // Use optimized repository method (compiled query + batch loading)
           var words = await _encodingRepository.GetWithEncodingStrengthAsync(
               pageNumber: State.CurrentPage,
               pageSize: 50,
               tagFilter: tagFilter,
               sortByEncodingStrength: sortByEncoding
           );
           
           SetState(s => s.VocabularyWords = words);
       }
   }
   ```

2. **Render Filter UI**:
   ```csharp
   VisualNode RenderFilterBar()
   {
       return HStack(spacing: MyTheme.Spacing80,
           // Tag filter dropdown
           Picker()
               .ItemsSource(State.AvailableTags)
               .SelectedItem(State.SelectedTag)
               .OnSelectedIndexChanged(OnTagFilterChanged),
           
           // Sort by encoding button
           Button($"{_localize["SortByEncoding"]}")
               .ThemeKey(MyTheme.Secondary)
               .OnClicked(() => SetState(s => s.SortByEncoding = !s.SortByEncoding))
       ).Padding(MyTheme.Spacing120);
   }
   ```

## Performance Testing

### Benchmark Tag Filtering

```csharp
[Fact]
public async Task TagFiltering_Should_Complete_Under_50ms()
{
    // Arrange: Populate database with 5000 words
    var words = GenerateTestWords(5000);
    await _context.VocabularyWords.AddRangeAsync(words);
    await _context.SaveChangesAsync();
    
    // Act: Filter by tag
    var stopwatch = Stopwatch.StartNew();
    var results = await _repository.FilterByTagAsync("nature");
    stopwatch.Stop();
    
    // Assert: Performance target met
    Assert.True(stopwatch.ElapsedMilliseconds < 50, 
        $"Tag filtering took {stopwatch.ElapsedMilliseconds}ms (target: <50ms)");
}
```

### Benchmark Encoding Strength Calculation

```csharp
[Fact]
public async Task EncodingStrength_Should_Calculate_100_Words_Under_30ms()
{
    // Arrange
    var words = GenerateTestWords(100);
    var counts = words.ToDictionary(w => w, w => Random.Shared.Next(0, 5));
    
    // Act
    var stopwatch = Stopwatch.StartNew();
    var scores = _calculator.CalculateBatch(counts);
    stopwatch.Stop();
    
    // Assert
    Assert.True(stopwatch.ElapsedMilliseconds < 30,
        $"Encoding calculation took {stopwatch.ElapsedMilliseconds}ms (target: <30ms)");
}
```

## Common Pitfalls

### ❌ WRONG: N+1 Query for Example Sentence Counts

```csharp
// Causes N queries (one per word)
foreach (var word in words)
{
    var count = await db.ExampleSentences
        .CountAsync(es => es.VocabularyWordId == word.Id);
    word.EncodingStrength = _calculator.Calculate(word, count);
}
```

### ✅ CORRECT: Batch Load Counts

```csharp
// Single query for all counts
var wordIds = words.Select(w => w.Id).ToList();
var counts = await db.ExampleSentences
    .Where(es => wordIds.Contains(es.VocabularyWordId))
    .GroupBy(es => es.VocabularyWordId)
    .Select(g => new { VocabularyWordId = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.VocabularyWordId, x => x.Count);

foreach (var word in words)
{
    var count = counts.GetValueOrDefault(word.Id, 0);
    word.EncodingStrength = _calculator.Calculate(word, count);
}
```

### ❌ WRONG: Tag Filtering Without Index

```csharp
// Table scan on 5000 words = slow
var filtered = await db.VocabularyWords
    .Where(w => w.Tags.Contains(tag))
    .ToListAsync();
```

### ✅ CORRECT: Use EF.Functions.Like with Index

```csharp
// Uses index IX_VocabularyWord_Tags for fast lookup
var filtered = await db.VocabularyWords
    .Where(w => EF.Functions.Like(w.Tags, $"%{tag}%"))
    .ToListAsync();
```

## Localization Keys

Add these keys to `Resources.resx` and `Resources.ko.resx`:

```xml
<!-- English (Resources.resx) -->
<data name="VocabLemmaLabel" xml:space="preserve">
  <value>Dictionary Form</value>
</data>
<data name="VocabTagsLabel" xml:space="preserve">
  <value>Tags</value>
</data>
<data name="VocabTagsPlaceholder" xml:space="preserve">
  <value>nature, season, visual (comma-separated)</value>
</data>
<data name="VocabMnemonicLabel" xml:space="preserve">
  <value>Memory Aid</value>
</data>
<data name="VocabImageUrlLabel" xml:space="preserve">
  <value>Mnemonic Image URL</value>
</data>
<data name="EncodingStrengthLabel" xml:space="preserve">
  <value>Encoding Strength</value>
</data>
<data name="EncodingStrengthBasic" xml:space="preserve">
  <value>Basic</value>
</data>
<data name="EncodingStrengthGood" xml:space="preserve">
  <value>Good</value>
</data>
<data name="EncodingStrengthStrong" xml:space="preserve">
  <value>Strong</value>
</data>
```

## Next Steps

1. Run migrations to update database schema
2. Implement repositories with compiled queries
3. Add UI components for editing encoding metadata
4. Test tag filtering performance with 5000+ words
5. Verify encoding strength calculations are accurate
6. Test cross-platform (iOS, Android, macOS, Windows)

**Remember**: Always use `ILogger<T>` for logging database operations and performance metrics!
