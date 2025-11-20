# Video Playback Activity - Specification

**Status**: Draft
**Author**: Claude Code
**Date**: 2025-11-20
**Target Release**: TBD

## Executive Summary

Add video playback capability to SentenceStudio, enabling learners to watch YouTube videos associated with learning resources. The feature integrates with the daily plan system and supports vocabulary lookup during playback.

## Goals

### Primary Goals
1. Enable video playback for learning resources with YouTube URLs
2. Integrate video watching as a trackable activity in daily plans
3. Provide consumption-focused UX optimized for language learning

### Stretch Goals
1. Synchronized transcript display with video playback
2. In-video vocabulary lookup and highlighting
3. Playback speed controls for comprehension
4. Subtitle/caption support

## Non-Goals
- Video hosting/storage (YouTube only for v1)
- Video editing or annotation
- Offline video playback
- Screen recording or video export

## User Stories

### Core Stories

**US-1: As a learner, I want to watch videos from my learning resources so I can practice listening comprehension with authentic content.**
- **Acceptance Criteria**:
  - Video player displays full-screen or optimized layout
  - Standard playback controls (play/pause, seek, volume)
  - Video loads from YouTube URL in learning resource
  - Activity time is tracked for daily plan progress

**US-2: As a learner, I want video watching to count toward my daily plan so I stay on track with my learning goals.**
- **Acceptance Criteria**:
  - "Video Watching" appears as activity option in daily plans
  - Only resources with valid video URLs are eligible
  - Time spent watching counts toward plan completion
  - Activity creates UserActivity record for progress tracking

**US-3: As a learner, I want to manually choose video watching as an activity so I can practice when I want.**
- **Acceptance Criteria**:
  - Video activity available in activity selection UI
  - Activity is disabled/hidden when selected resource has no video URL
  - Clear indication when video is available vs unavailable

### Stretch Stories

**US-4: As a learner, I want to see the transcript synchronized with the video so I can read along while listening.**
- **Acceptance Criteria**:
  - Transcript displays below or beside video
  - Current sentence highlights as video plays
  - Clicking transcript sentence seeks video to that timestamp
  - Auto-scrolls to keep current sentence visible

**US-5: As a learner, I want to look up vocabulary words while watching so I can understand unfamiliar terms.**
- **Acceptance Criteria**:
  - Tap/click word in transcript to see definition
  - Quick popup shows vocabulary info if word is known
  - Option to add unknown words to vocabulary list
  - Playback pauses during lookup (optional)

## Technical Design

### Architecture Overview

```
┌─────────────────────────────────────────────────┐
│           VideoWatchingPage (MauiReactor)       │
│  ┌───────────────────────────────────────────┐  │
│  │         YouTube Player (WebView)          │  │
│  │  - CommunityToolkit.Maui.MediaElement OR  │  │
│  │  - WebView with YouTube embed             │  │
│  └───────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────┐  │
│  │     Transcript Display (Stretch)          │  │
│  │  - ScrollView with sentence highlighting  │  │
│  │  - Word tap detection                     │  │
│  └───────────────────────────────────────────┘  │
│  ┌───────────────────────────────────────────┐  │
│  │         Playback Controls                 │  │
│  │  - Play/Pause, Speed, Fullscreen          │  │
│  └───────────────────────────────────────────┘  │
└─────────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────┐
│           VideoWatchingService                  │
│  - ExtractYouTubeVideoId(url)                   │
│  - ValidateVideoUrl(url)                        │
│  - GetEmbedUrl(videoId)                         │
│  - TrackWatchingProgress(resourceId, duration)  │
└─────────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────┐
│         Existing Infrastructure                 │
│  - LearningResourceRepository                   │
│  - UserActivityRepository                       │
│  - ActivityTimerService                         │
│  - Daily Plan System                            │
└─────────────────────────────────────────────────┘
```

### Data Model Changes

#### LearningResource (Existing - No Changes Required)
```csharp
public class LearningResource
{
    public int Id { get; set; }
    public string Title { get; set; }
    public string? MediaUrl { get; set; }  // ← Already exists! YouTube URL goes here
    public MediaType MediaType { get; set; } // ← Add Video to enum
    public string? Transcript { get; set; }  // ← Already exists for read-along
    public string Language { get; set; }
    // ... other properties
}
```

#### MediaType Enum Update
```csharp
public enum MediaType
{
    None = 0,
    Audio = 1,
    Video = 2,  // ← ADD THIS
    Image = 3,
    Document = 4
}
```

#### Activity Enum Update
```csharp
public enum Activity
{
    // ... existing activities
    VideoWatching = 11  // ← ADD THIS
}
```

### Component Design

#### VideoWatchingPage.cs
```csharp
namespace SentenceStudio.Pages.VideoWatching;

/// <summary>
/// Page for video watching activity with optional transcript synchronization.
/// </summary>
partial class VideoWatchingPage : Component<VideoWatchingPageState, ActivityProps>
{
    [Inject] VideoWatchingService _videoService;
    [Inject] UserActivityRepository _activityRepository;
    [Inject] IActivityTimerService _timerService;
    [Inject] LearningResourceRepository _resourceRepository;

    public override VisualNode Render() =>
        ContentPage("Video Watching",
            Grid(rows: "*, Auto", columns: "*",
                RenderVideoPlayer(),      // Row 0: Video player (YouTube native controls)
                RenderTranscript(),       // Row 1: Transcript (stretch goal)
                LoadingOverlay()
            )
        )
        .Set(MauiControls.Shell.TitleViewProperty,
             Props?.FromTodaysPlan == true ? new ActivityTimerBar() : null)
        .OnAppearing(OnPageAppearing);

    private VisualNode RenderVideoPlayer() =>
        // WebView with YouTube iframe embed
        // YouTube provides all playback controls (play/pause/seek/volume/fullscreen/speed)
        WebView()
            .Source(State.VideoEmbedUrl)
            .OnNavigating(OnWebViewNavigating)
            .GridRow(0);
}
```

#### VideoWatchingService.cs
```csharp
namespace SentenceStudio.Services;

/// <summary>
/// Service for managing video playback and YouTube integration.
/// </summary>
public class VideoWatchingService
{
    private readonly ILogger<VideoWatchingService> _logger;
    private readonly LearningResourceRepository _resourceRepository;
    private readonly UserActivityRepository _activityRepository;

    public VideoWatchingService(IServiceProvider services)
    {
        _logger = services.GetRequiredService<ILogger<VideoWatchingService>>();
        _resourceRepository = services.GetRequiredService<LearningResourceRepository>();
        _activityRepository = services.GetRequiredService<UserActivityRepository>();
    }

    /// <summary>
    /// Extracts YouTube video ID from various YouTube URL formats.
    /// </summary>
    public string? ExtractYouTubeVideoId(string url)
    {
        // Handles:
        // - https://www.youtube.com/watch?v=VIDEO_ID
        // - https://youtu.be/VIDEO_ID
        // - https://www.youtube.com/embed/VIDEO_ID
        // Returns: VIDEO_ID or null if invalid
    }

    /// <summary>
    /// Validates that a video URL is playable.
    /// </summary>
    public async Task<bool> ValidateVideoUrlAsync(string url)
    {
        // Check if URL is accessible
        // Check if video ID can be extracted
        // Optional: Ping YouTube API to verify video exists
    }

    /// <summary>
    /// Gets YouTube embed URL for iframe.
    /// </summary>
    public string GetEmbedUrl(string videoId)
    {
        // Returns: https://www.youtube.com/embed/VIDEO_ID?modestbranding=1&rel=0
        // No autoplay - user must click play
        // Uses YouTube's native controls for all playback functions
    }

    /// <summary>
    /// Tracks video watching activity for progress.
    /// </summary>
    public async Task TrackWatchingActivityAsync(
        int resourceId,
        TimeSpan duration)
    {
        await _activityRepository.SaveAsync(new UserActivity
        {
            Activity = Activity.VideoWatching,
            Input = $"Resource {resourceId}",
            CreatedAt = DateTime.UtcNow
        });
    }
}
```

### Platform-Specific Considerations

#### YouTube Embedding Options

**Option 1: WebView with YouTube iframe (RECOMMENDED for v1)**
- **Pros**:
  - Works cross-platform (iOS, Android, macOS, Windows)
  - No API keys required
  - YouTube handles all playback
  - Supports all YouTube features (captions, speed, quality)
- **Cons**:
  - Less control over player
  - Requires internet connection
  - Harder to sync with transcript

**Option 2: MediaElement with extracted video URL**
- **Pros**:
  - Native playback controls
  - Better performance
  - Can sync events with transcript
- **Cons**:
  - Requires YouTube API or extraction library
  - May violate YouTube TOS
  - More complex implementation

**Option 3: Platform-specific native players**
- **Pros**:
  - Best performance
  - Full control
- **Cons**:
  - Complex platform-specific code
  - Maintenance burden

**Decision**: Use **Option 1 (WebView)** for v1, consider Option 2 for v2 if transcript sync is essential.

#### WebView YouTube Embed Implementation

```html
<!-- Embedded in WebView.Source -->
<!DOCTYPE html>
<html>
<head>
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <style>
        body { margin: 0; padding: 0; background: #000; }
        .video-container { position: relative; width: 100%; padding-bottom: 56.25%; }
        iframe { position: absolute; top: 0; left: 0; width: 100%; height: 100%; border: 0; }
    </style>
</head>
<body>
    <div class="video-container">
        <iframe
            src="https://www.youtube.com/embed/{VIDEO_ID}?modestbranding=1&rel=0"
            allow="accelerometer; clipboard-write; encrypted-media; gyroscope"
            allowfullscreen>
        </iframe>
    </div>
    <!--
        Note: No autoplay parameter - user must click play
        Note: YouTube's native controls handle play/pause/seek/volume/fullscreen/speed
        Note: No custom playback controls needed
    -->
</body>
</html>
```

### Integration Points

#### 1. Daily Plan Integration

**Plan Generation** (`LlmPlanGenerationService.cs`)
```csharp
// Add video watching as eligible activity type
private List<string> GetEligibleActivities(List<LearningResource> resources)
{
    var activities = new List<string>();

    // Existing activities...

    // Add video watching if any resource has video
    if (resources.Any(r => r.MediaType == MediaType.Video &&
                          !string.IsNullOrEmpty(r.MediaUrl)))
    {
        activities.Add("VideoWatching");
    }

    return activities;
}
```

**Plan Item Creation**
```csharp
var planItem = new DailyPlanItem
{
    TitleKey = "plan_item_video_watching_title",
    DescriptionKey = "plan_item_video_watching_desc",
    ActivityType = Activity.VideoWatching,
    ResourceId = resource.Id,
    EstimatedMinutes = 10,  // Configurable based on video length
    Route = "videowatching",
    RouteParametersJson = JsonSerializer.Serialize(new ActivityProps
    {
        Resource = resource,
        Skill = skill,
        FromTodaysPlan = true,
        PlanItemId = planItem.Id
    })
};
```

#### 2. Activity Selection UI

**Dashboard Activity Cards** (`DashboardPage.cs`)
```csharp
private VisualNode RenderActivityCard(string activityType)
{
    var isVideoEnabled = activityType == "VideoWatching" &&
        State.SelectedResources.Any(r =>
            r.MediaType == MediaType.Video &&
            !string.IsNullOrEmpty(r.MediaUrl));

    return ActivityBorder()
        .IsEnabled(isVideoEnabled)
        .Opacity(isVideoEnabled ? 1.0 : 0.5)
        // ... rest of card UI
}
```

#### 3. Resource Validation

**Resource Creation/Edit** (`AddLearningResourcePage.cs`, `EditLearningResourcePage.cs`)
```csharp
private async Task ValidateAndSaveResource()
{
    // When MediaType is Video, validate MediaUrl
    if (State.MediaType == MediaType.Video &&
        !string.IsNullOrEmpty(State.MediaUrl))
    {
        var videoId = _videoService.ExtractYouTubeVideoId(State.MediaUrl);
        if (videoId == null)
        {
            await ShowError("Invalid YouTube URL");
            return;
        }

        State.MediaUrl = $"https://www.youtube.com/watch?v={videoId}";
    }

    await SaveResource();
}
```

### Localization

**Strings to add** (`Resources/Strings/en.json`)
```json
{
    "VideoWatching": "Video Watching",
    "video_watching_desc": "Watch videos to improve listening comprehension",
    "plan_item_video_watching_title": "Watch Video",
    "plan_item_video_watching_desc": "Watch and listen to authentic content",
    "no_video_available": "No video available for this resource",
    "video_loading": "Loading video...",
    "video_error": "Unable to load video"
}
```

## User Experience

### UX Flow 1: Daily Plan Video Activity

```
[Dashboard: Today's Plan]
    │
    ├─ Shows "Watch Video" activity (10 min)
    │  └─ Resource: "Visual Korean - Bali Trip"
    │
    ▼ User taps activity
    │
[VideoWatchingPage]
    │
    ├─ Timer starts (ActivityTimerService)
    ├─ Video loads in WebView
    ├─ User watches video
    │  └─ Can pause, seek, adjust volume
    │
    ▼ User completes or navigates away
    │
[Dashboard]
    │
    └─ Activity marked complete (10 min logged)
```

### UX Flow 2: Manual Activity Selection

```
[Dashboard]
    │
    ▼ User selects resource with video
    │
[Activity Selection UI]
    │
    ├─ "Video Watching" card is ENABLED
    ├─ "Shadowing" card is enabled
    ├─ "Reading" card is enabled
    │
    ▼ User taps "Video Watching"
    │
[VideoWatchingPage]
    │
    └─ Same as Flow 1, no timer bar
```

### UX Flow 3: Resource Without Video

```
[Dashboard]
    │
    ▼ User selects resource WITHOUT video
    │
[Activity Selection UI]
    │
    ├─ "Video Watching" card is DISABLED/HIDDEN
    │  └─ Tooltip: "No video available for this resource"
    ├─ "Shadowing" card is enabled
    ├─ "Reading" card is enabled
```

### Layout Design

#### Portrait Phone Layout
```
┌─────────────────────────┐
│  [Timer Bar if plan]    │ ← ActivityTimerBar
├─────────────────────────┤
│                         │
│    Video Player Area    │ ← 16:9 aspect ratio
│     (YouTube embed)     │
│                         │
├─────────────────────────┤
│  Resource: Video Title  │ ← Resource info
├─────────────────────────┤
│  [Transcript] (stretch) │
│  Lorem ipsum dolor sit  │ ← ScrollView
│  amet, consectetur...   │    with sentences
│  *Highlighted sentence* │
│  ...                    │
└─────────────────────────┘
```

#### Landscape/Tablet Layout
```
┌──────────────────────────────────────────────┐
│           [Timer Bar if plan]                │
├────────────────────┬─────────────────────────┤
│                    │  Resource: Title        │
│   Video Player     │  ───────────────────    │
│   (YouTube embed)  │  [Transcript] (stretch) │
│                    │  Lorem ipsum dolor sit  │
│                    │  amet, consectetur...   │
│                    │  *Highlighted sentence* │
│                    │  ...                    │
└────────────────────┴─────────────────────────┘
```

## Implementation Phases

### Phase 1: Core Video Playback (MVP)
**Estimated Effort**: 3-5 days

#### Tasks
- [ ] Add `Video` to `MediaType` enum
- [ ] Add `VideoWatching` to `Activity` enum
- [ ] Create `VideoWatchingService` with URL parsing
- [ ] Create `VideoWatchingPage` component with WebView
- [ ] Register route in `MauiProgram.cs`
- [ ] Integrate with `ActivityTimerService` for tracking
- [ ] Add localization strings
- [ ] Test on iOS, Android, macOS, Windows

#### Testing Checklist
- [ ] YouTube URL parsing works for all formats
- [ ] Video loads and plays in WebView
- [ ] Playback controls work (play/pause, seek, volume)
- [ ] Timer tracks watching time correctly
- [ ] Activity saves to UserActivity table
- [ ] Video works on all target platforms
- [ ] Handles invalid/missing URLs gracefully

### Phase 2: Daily Plan Integration
**Estimated Effort**: 2-3 days

#### Tasks
- [ ] Update `LlmPlanGenerationService` to include video activities
- [ ] Add video watching to plan generation prompts
- [ ] Update dashboard activity cards to show/hide video option
- [ ] Add video watching option to manual activity selection
- [ ] Test plan generation with video resources
- [ ] Test plan completion tracking

#### Testing Checklist
- [ ] Plans include video activities when resources have videos
- [ ] Video activity disabled when no video available
- [ ] Completing video activity marks plan item as done
- [ ] Time tracking integrates with daily plan progress

### Phase 3: Resource Management
**Estimated Effort**: 1-2 days

#### Tasks
- [ ] Update `AddLearningResourcePage` with MediaType picker
- [ ] Add YouTube URL validation on save
- [ ] Update `EditLearningResourcePage` with video support
- [ ] Add UI indicators for video-enabled resources
- [ ] Import existing resources and set MediaType

#### Testing Checklist
- [ ] Can create resources with video URLs
- [ ] URL validation prevents invalid YouTube URLs
- [ ] Can edit video URLs for existing resources
- [ ] Resource list shows video indicator

### Phase 4: Transcript Synchronization (Stretch)
**Estimated Effort**: 5-7 days

#### Tasks
- [ ] Design transcript-video sync architecture
- [ ] Implement YouTube iframe API for playback events
- [ ] Add JavaScript bridge between WebView and MAUI
- [ ] Create transcript display with sentence highlighting
- [ ] Implement auto-scroll for transcript
- [ ] Add click-to-seek functionality
- [ ] Add timestamp parsing for transcript sentences

#### Technical Notes
Requires YouTube iframe API:
```javascript
// In WebView HTML
<script src="https://www.youtube.com/iframe_api"></script>
<script>
    var player;
    function onYouTubeIframeAPIReady() {
        player = new YT.Player('player', {
            events: {
                'onStateChange': onPlayerStateChange
            }
        });
    }

    function onPlayerStateChange(event) {
        // Send time updates to MAUI
        window.chrome.webview.postMessage({
            type: 'timeupdate',
            currentTime: player.getCurrentTime()
        });
    }
</script>
```

### Phase 5: Vocabulary Lookup (Stretch)
**Estimated Effort**: 3-4 days

#### Tasks
- [ ] Add tap/click detection on transcript words
- [ ] Integrate with existing vocabulary system
- [ ] Create vocabulary popup UI
- [ ] Add "pause on lookup" option
- [ ] Add "add to vocabulary" from transcript
- [ ] Test word segmentation for CJK languages

## Technical Considerations

### Performance
- **WebView Loading**: Add loading indicator while video initializes
- **Memory**: Monitor memory usage during video playback
- **Battery**: Video playback is power-intensive, consider battery optimization

### Security
- **URL Validation**: Only allow youtube.com and youtu.be domains
- **Content Security Policy**: Set appropriate CSP headers in WebView
- **User Privacy**: No tracking beyond local activity recording

### Accessibility
- **Captions**: Leverage YouTube's native caption support
- **Screen Readers**: Provide alt text for video player area
- **Keyboard Navigation**: Ensure playback controls are keyboard accessible

### Error Handling
- Network failures during video load
- Invalid or deleted YouTube videos
- Geo-restricted content
- Age-restricted content
- Private videos

## Success Metrics

### Core Metrics
- **Adoption Rate**: % of users who try video watching activity
- **Completion Rate**: % of started video sessions that are completed
- **Daily Plan Integration**: % of plans that include video activities
- **Time Spent**: Average minutes spent watching videos per user per day

### Stretch Metrics (if transcript sync implemented)
- **Transcript Engagement**: % of users who interact with transcript
- **Vocabulary Lookups**: Number of words looked up during video watching
- **Seek Usage**: Number of times users click transcript to seek video

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| YouTube TOS violations | High | Use official YouTube embed, follow TOS |
| Platform WebView inconsistencies | Medium | Test thoroughly on all platforms |
| Video availability changes | Medium | Implement graceful error handling |
| Transcript sync complexity | Medium | Make it optional stretch goal |
| Performance issues | Low | Monitor and optimize WebView usage |

## Design Decisions

1. **Video Length Limits**: ✅ No limit - allow videos of any length
2. **Autoplay**: ✅ No autoplay - user must manually start video
3. **Playback Controls**: ✅ Use YouTube's native player controls only
4. **Picture-in-Picture**: ✅ Not supported in v1 - rely on YouTube's native PiP if available
5. **Download for Offline**: ⏳ Future consideration for offline playback
6. **Multiple Videos per Resource**: ⏳ Single video URL per resource for v1

## Future Enhancements

### v2 Features
- Video playback analytics (watch time, completion rate)
- Bookmarking specific video timestamps
- Repeat loops for difficult sections
- A-B repeat for focused practice
- Note-taking during playback

### v3 Features
- Support for non-YouTube platforms (Vimeo, etc.)
- Video upload and hosting
- Interactive quizzes embedded in video
- Community-contributed timestamps
- Video responses (record yourself shadowing)

## References

### Technical Documentation
- [.NET MAUI WebView](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/webview)
- [YouTube iframe API](https://developers.google.com/youtube/iframe_api_reference)
- [CommunityToolkit.Maui MediaElement](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/maui/views/mediaelement)
- [YouTube Terms of Service](https://www.youtube.com/static?template=terms)

### Related Specs
- Daily Plan System (existing)
- Activity Timer Service (existing)
- Shadowing Activity (recently implemented)
- Transcript-based Learning (existing)

## Appendix

### Example YouTube URL Formats
```
Standard:
https://www.youtube.com/watch?v=dQw4w9WgXcQ

Short:
https://youtu.be/dQw4w9WgXcQ

Embedded:
https://www.youtube.com/embed/dQw4w9WgXcQ

With timestamp:
https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s
https://youtu.be/dQw4w9WgXcQ?t=90

Mobile:
https://m.youtube.com/watch?v=dQw4w9WgXcQ
```

### Video ID Extraction Regex
```csharp
private static readonly Regex[] YoutubeRegexes = new[]
{
    new Regex(@"youtube\.com/watch\?v=([a-zA-Z0-9_-]{11})"),
    new Regex(@"youtu\.be/([a-zA-Z0-9_-]{11})"),
    new Regex(@"youtube\.com/embed/([a-zA-Z0-9_-]{11})"),
    new Regex(@"youtube\.com/v/([a-zA-Z0-9_-]{11})")
};
```

### Sample Learning Resource with Video
```json
{
    "id": 42,
    "title": "Visual Korean Listening - A Day in Bali",
    "language": "Korean",
    "mediaType": "Video",
    "mediaUrl": "https://www.youtube.com/watch?v=ABC123XYZ",
    "transcript": "안녕하세요. 오늘은 발리에서의 하루를 소개할게요...",
    "description": "Follow along as we explore Bali and practice Korean listening",
    "tags": ["beginner", "travel", "listening"],
    "vocabulary": [...]
}
```
