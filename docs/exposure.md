 🎯 The Core Problem

   Ye're absolutely right - without tracking exposure, the system can't enforce the
   research-backed learning sequence. Currently, learners can:

     - Import vocabulary from a YouTube video
     - Immediately jump to VocabularyQuiz
     - Get tested on words they've never actually encountered in context

   This violates the Input Hypothesis fundamentally!

   -------------------------------------------------------------------------------

   📊 Solution: Exposure Tracking & Gated Progression

   1. Track Exposure Events

   Create an ExposureLog to track every meaningful encounter:

     public class VocabularyExposure
     {
         public int Id { get; set; }
         public int UserId { get; set; }
         public int VocabularyWordId { get; set; }
         public int LearningResourceId { get; set; }
         public DateTime ExposureDateTime { get; set; }
         public ExposureType Type { get; set; } // Reading, Listening, Video
         public bool WasContextual { get; set; } // In sentence vs. isolated
         public int? TimeSpentSeconds { get; set; }
     }

     public enum ExposureType
     {
         ReadingWithAudio,      // Most powerful - dual mode
         ReadingOnly,
         ListeningOnly,
         VideoWithSubtitles,
         IsolatedFlashcard      // Least powerful
     }

   2. Define Exposure Requirements Per Word

   Before testing is allowed, require:

     public class ExposureRequirements
     {
         // Minimum exposures before testing
         public const int MIN_EXPOSURES_FOR_RECOGNITION = 3;
         public const int MIN_EXPOSURES_FOR_PRODUCTION = 5;

         // At least one must be contextual (in sentence/story)
         public const bool REQUIRE_CONTEXTUAL_EXPOSURE = true;

         // Time-based: exposures should be spread over time
         public static TimeSpan MIN_TIME_BETWEEN_EXPOSURES = TimeSpan.FromHours(4);
     }

   Research basis:

     - Nation (2001): 8-12 exposures for initial learning
     - We're being conservative with 3-5 to start, can adjust based on data
     - Spaced exposures work better than massed (Cepeda et al., 2006)

   -------------------------------------------------------------------------------

   🎮 Implementation Strategy

   Phase 1: Passive Tracking (Non-Blocking)

   Start by tracking WITHOUT blocking activities:

     - Automatic exposure logging when user:
       - Watches a video with the learning resource
       - Reads text in ReadingPage
       - Listens to audio in ShadowingPage
       - Views vocabulary in DescribeAScene
     - Visual indicators show exposure status:  Word: 사과 (sagwa)
       Exposures: 2/3 needed 📚📚⚪
       Last seen: 2 hours ago
       Context: ✅ Seen in sentence

       Status: Need 1 more exposure before quiz recommended
     - Soft recommendations (not blocking):  ⚠️ Low Exposure Warning

       You have 15 words with fewer than 3 exposures.
       We recommend reviewing the source material before testing.

       [Review Learning Resource] [Continue Anyway]

   Advantage: Doesn't disrupt current users, gathers data on natural usage patterns

   -------------------------------------------------------------------------------

   Phase 2: Smart Activity Recommendations

   Add an Activity Recommendation Engine:

     public class ActivityRecommendationService
     {
         public ActivityRecommendation GetNextActivity(
             int userId,
             int learningResourceId)
         {
             var words = GetWordsForResource(learningResourceId);
             var exposures = GetExposureCounts(userId, words);

             // Check exposure status
             var needsExposure = words.Where(w =>
                 exposures[w.Id] < ExposureRequirements.MIN_EXPOSURES_FOR_RECOGNITION
             ).ToList();

             if (needsExposure.Any())
             {
                 return new ActivityRecommendation
                 {
                     Activity = ActivityType.ReadingWithAudio,
                     Reason = "Build familiarity with new vocabulary",
                     Priority = Priority.High,
                     WordsAffected = needsExposure.Count
                 };
             }

             // Check if ready for recognition testing
             var readyForRecognition = words.Where(w =>
                 exposures[w.Id] >= 3 &&
                 GetProgressPhase(w) < LearningPhase.Recognition
             ).ToList();

             if (readyForRecognition.Any())
             {
                 return new ActivityRecommendation
                 {
                     Activity = ActivityType.VocabularyQuiz,
                     Reason = "Test recognition of familiar words",
                     Priority = Priority.Medium,
                     WordsAffected = readyForRecognition.Count
                 };
             }

             // Continue with production activities...
         }
     }

   UI Implementation:

   On the home/dashboard:

     📚 Learning Resource: "Korean Street Food Episode 3"
     Progress: 45/80 words learned

     🎯 RECOMMENDED NEXT ACTIVITY

     ┌─────────────────────────────────────────┐
     │  📖 Reading Practice                     │
     │  ⭐ HIGHLY RECOMMENDED                   │
     │                                         │
     │  25 words need more exposure before     │
     │  you can effectively practice them.     │
     │                                         │
     │  This will help you:                    │
     │  • See words in natural context        │
     │  • Build recognition before production │
     │  • Improve retention by 3x             │
     │                                         │
     │  [Start Reading] [Skip (Not Advised)]  │
     └─────────────────────────────────────────┘

     Other Activities (Available when ready):
       🎯 Vocabulary Quiz ━━━━⚪⚪⚪⚪ Need 3 more exposures
       ✏️  Clozure Practice ━━━━⚪⚪⚪⚪ Need 3 more exposures
       🗣️  Describe Scene   ━━━━━━━⚪ Need recognition firs

   -------------------------------------------------------------------------------

   Phase 3: Gated Progression (Optional/Settings-Based)

   Add a Learning Mode setting:

     public enum LearningMode
     {
         Guided,      // Recommended activities, soft warnings
         Structured,  // Activities locked until requirements met
         Free         // No restrictions (current behavior)
     }

   In Structured Mode, activities show lock states:

     Available Activities for "Korean Street Food Ep 3"

     ✅ Reading with Audio
        Ready to start • 25 new words

     🔒 Vocabulary Quiz
        Unlock after 3 exposures per word
        Current: 0/25 words ready

     🔒 Clozure Practice
        Unlock after 3 exposures per word
        Current: 0/25 words ready

     🔒 Vocabulary Matching
        Unlock after 3 exposures per word
        Current: 0/25 words ready

     🔒 Describe a Scene
        Unlock after achieving recognition mastery
        Current: 0/25 words ready

   -------------------------------------------------------------------------------

   📖 New Activity: Structured Exposure Session

   Add a dedicated "Pre-Learn" activity that's DESIGNED for initial exposure:

   Activity: "Vocabulary Preview"

     ┌──────────────────────────────────────────────┐
     │  Vocabulary Preview                           │
     │  Building familiarity before practice        │
     │  Word 5 of 25                                │
     └──────────────────────────────────────────────┘

     ┌──────────────────────────────────────────────┐
     │                                              │
     │         🍎 사과 (sagwa)                      │
     │             apple                            │
     │                                              │
     │  [🔊 Listen to pronunciation]               │
     │                                              │
     │  ───────────────────────────────────────    │
     │                                              │
     │  Example Sentence:                           │
     │  "나는 사과를 좋아해요."                      │
     │  (I like apples.)                            │
     │                                              │
     │  [🔊 Listen to sentence]                    │
     │                                              │
     │  ───────────────────────────────────────    │
     │                                              │
     │  More Examples:                              │
     │  • "이 사과는 아주 달아요." (sweet)          │
     │  • "사과 주스를 마셨어요." (juice)           │
     │                                              │
     │  [🔊 Hear all examples]                     │
     │                                              │
     └──────────────────────────────────────────────┘

     [Previous] [I know this] [Next (5s)]

   Key Features:

     - NO TESTING - pure exposure
     - Multiple contexts automatically shown
     - Audio for pronunciation modeling
     - Option to skip known words
     - Auto-advances to prevent "studying" behavior
     - Logs exposure automatically

   When to trigger:

     - Automatically offered when new learning resource added
     - Suggested when words have < 3 exposures
     - Can be manually accessed anytime

   -------------------------------------------------------------------------------

   🎓 Enhanced Learning Resource Flow

   Modify the Learning Resource experience:

   Current Flow:

     1. User selects YouTube video
     2. System extracts vocabulary
     3. User immediately sees all activities available

   Improved Flow:

     1. User selects YouTube video
     2. System extracts vocabulary

     3. ONBOARDING PROMPT:
        ┌─────────────────────────────────────────┐
        │  Great choice! This video contains      │
        │  80 vocabulary words.                   │
        │                                         │
        │  📚 For best results, we recommend:     │
        │                                         │
        │  1. Watch the full video first (12min) │
        │  2. Review vocabulary previews (8min)  │
        │  3. Read the transcript (10min)        │
        │  4. Then practice with activities      │
        │                                         │
        │  This sequence improves retention 3x!   │
        │                                         │
        │  [Follow Recommendation] [Skip to Quiz] │
        └─────────────────────────────────────────┘

     4. If "Follow Recommendation":
        a. Play video (tracks viewing completion)
        b. Launch Vocabulary Preview (logs exposures)
        c. Launch Reading with Audio (logs exposures)
        d. THEN unlock testing activities

     5. Dashboard shows exposure progress:
        Words ready for practice: 0 → 15 → 45 → 80

   -------------------------------------------------------------------------------

   📊 Tracking Exposure from Source Material

   For YouTube Videos:

     public class VideoWatchTracker
     {
         public async Task TrackVideoWatching(
             int userId,
             int learningResourceId,
             TimeSpan watchDuration)
         {
             var resource = await GetLearningResource(learningResourceId);
             var vocabularyWords = await GetVocabularyForResource(learningResourceId);

             // If user watched 80%+ of video
             if (watchDuration.TotalSeconds / resource.DurationSeconds > 0.8)
             {
                 // Log exposure for ALL words in that resource
                 foreach (var word in vocabularyWords)
                 {
                     await LogExposure(userId, word.Id,
                         ExposureType.VideoWithSubtitles,
                         contextual: true);
                 }
             }
         }
     }

   For Reading Materials:

     public class ReadingTracker
     {
         // Track which sentences user has seen
         public async Task TrackSentenceRead(
             int userId,
             int sentenceId,
             TimeSpan timeSpent)
         {
             // If user spent reasonable time (not skipping)
             if (timeSpent.TotalSeconds > 2)
             {
                 var wordsInSentence = await GetVocabularyInSentence(sentenceId);

                 foreach (var word in wordsInSentence)
                 {
                     await LogExposure(userId, word.Id,
                         ExposureType.ReadingWithAudio,
                         contextual: true);
                 }
             }
         }
     }

   -------------------------------------------------------------------------------

   🎯 Recommended Implementation Order

   Sprint 1: Foundation

     - Add VocabularyExposure table and repository
     - Implement exposure logging in ReadingPage
     - Add exposure count display to vocabulary progress view
     - Log exposures from existing activities (reading, shadowing)

   Sprint 2: Recommendations

     - Build ActivityRecommendationService
     - Add dashboard widget showing recommended next activity
     - Implement soft warnings for low-exposure words
     - Add exposure progress bar to learning resources

   Sprint 3: New Activity

     - Create "Vocabulary Preview" activity
     - Auto-trigger for new learning resources
     - Add to activity carousel with high priority
     - Track completion and exposure logging

   Sprint 4: Video/Audio Tracking

     - Add video watch duration tracking
     - Implement bulk exposure logging for completed videos
     - Add "Have you watched/listened?" prompt
     - Track podcast/audio completion

   Sprint 5: Gated Progression (Optional)

     - Add LearningMode user setting
     - Implement activity locking logic
     - Add unlock indicators and progress paths
     - Create "why locked?" explanatory modals

   -------------------------------------------------------------------------------

   📈 Success Metrics

   Track these to validate the approach:

     - Exposure → Retention Correlation
       - Do words with 3+ exposures have higher mastery rates?
       - Target: 2-3x improvement in first-attempt accuracy
     - Time to Mastery
       - Does pre-exposure reduce total attempts needed?
       - Target: 30% reduction in attempts to mastery
     - User Engagement
       - Do users follow recommendations?
       - Retention rate for structured vs. free mode?
     - Frustration Indicators
       - Reduced "first attempt failures" on quizzes
       - Lower early abandonment of activities

   -------------------------------------------------------------------------------

   🏴‍☠️ Captain's Action P

   Immediate (Week 1):

     - Add exposure tracking to ReadingPage
     - Display exposure counts on vocabulary detail view
     - Add manual "Mark as Exposed" button for videos/podcasts user consumed

   Short-term (Month 1):

     - Build recommendation engine
     - Add dashboard recommendations
     - Create Vocabulary Preview activity
     - Soft warnings for low-exposure testing

   Long-term (Quarter 1):

     - Full video/audio watch tracking
     - Gated progression as opt-in feature
     - Machine learning to optimize exposure thresholds
     - A/B test different exposure requirements

   -------------------------------------------------------------------------------

   The Key Insight: Ye need to make the invisible visible. Users don't realize
   they're skipping crucial exposure steps. By tracking, visualizing, and gently
   guiding them through the evidence-based sequence, ye'll dramatically improve
   learning outcomes while respecting user autonomy!
