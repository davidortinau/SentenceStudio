# Feature Specification: Minimal Pairs Listening Activity

**Feature Branch**: `001-minimal-pairs`  
**Created**: 2025-12-14  
**Status**: Draft  
**Input**: User description: "Add an activity for minimal pairs to improve listening identification skills for Korean. The activity plays audio for a word and I choose which of the pair it was, with immediate correctness feedback and a progress summary (right vs wrong)."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Practice minimal pair identification (Priority: P1)

As a learner, I want to listen to a short audio prompt and choose between two very similar options so I can train my ability to distinguish confusing Korean sounds.

**Why this priority**: This is the core learning value: repeated, fast identification with feedback.

**Independent Test**: Can be fully tested by starting a session, answering prompts, and verifying immediate feedback + right/wrong counts.

**Acceptance Scenarios**:

1. **Given** a minimal pair is selected, **When** I tap “Start”, **Then** the app plays an audio prompt and shows exactly two answer choices.
2. **Given** a prompt is active, **When** I pick an answer, **Then** I immediately see whether it was correct and the running right/wrong counters update.
3. **Given** I answered a prompt, **When** I continue, **Then** the next prompt is presented without requiring me to re-select the pair.
4. **Given** I need to hear it again, **When** I tap “Replay”, **Then** the same prompt audio plays again without advancing.

---

### User Story 2 - See session results and progress summary (Priority: P2)

As a learner, I want to see a summary of my performance so I know whether I’m improving.

**Why this priority**: Without results, the activity is less motivating and it’s harder to self-correct.

**Independent Test**: Can be fully tested by completing a short session and verifying the end summary reflects the same right/wrong outcomes seen during the session.

**Acceptance Scenarios**:

1. **Given** I have answered at least one prompt, **When** I end the session, **Then** I see totals for correct, incorrect, and accuracy percentage.
2. **Given** I complete multiple sessions for the same pair, **When** I view progress, **Then** I can see a trend over time (e.g., accuracy history by session).

---

### User Story 3 - Choose practice mode (focus vs mixed) (Priority: P3)

As a learner, I want to either focus on one minimal pair or practice a mixed set so I can balance confidence-building and real-world discrimination.

**Why this priority**: Learners often need “focus mode” early, but practice typically benefits from mixing once the task is understood.

**Independent Test**: Can be tested by switching modes and confirming prompts are drawn according to the selected mode.

**Acceptance Scenarios**:

1. **Given** I am starting a session, **When** I choose “Focus on one pair”, **Then** the session uses only that pair until I change it.
2. **Given** I am starting a session, **When** I choose “Mixed practice”, **Then** prompts are drawn from a set of minimal pairs during the session.

---

### User Story 4 - Create and manage minimal pairs from vocabulary (Priority: P2)

As a learner, I want to create a minimal pair by selecting two existing vocabulary words so I can practice real words I’m learning.

**Why this priority**: Without a way to define pairs using my vocabulary, the activity can’t be personalized and is harder to scale.

**Independent Test**: Can be tested by creating a minimal pair from two vocabulary words, selecting it for practice, and verifying it appears consistently.

**Acceptance Scenarios**:

1. **Given** I have at least two vocabulary words, **When** I create a minimal pair by selecting two words, **Then** the pair is saved and can be selected for practice.
2. **Given** I am creating a minimal pair, **When** I provide an optional label for what sound contrast I’m practicing, **Then** that label appears wherever the pair is shown.
3. **Given** a minimal pair exists, **When** I edit or remove the pair, **Then** the app updates selection lists and does not break existing session history views.

---

### User Story 5 - View minimal pair history and success rate (Priority: P2)

As a learner, I want to see my history and success rate for a specific minimal pair so I can understand whether I’m improving on that sound contrast.

**Why this priority**: Minimal pairs are only useful if I can track accuracy and identify what still confuses me.

**Independent Test**: Can be tested by completing sessions on a pair and verifying per-pair stats match recorded attempts.

**Acceptance Scenarios**:

1. **Given** I practiced a minimal pair, **When** I view the pair’s details, **Then** I can see overall correct, incorrect, and accuracy.
2. **Given** I practiced a minimal pair across multiple sessions, **When** I view its history, **Then** I can see session-by-session results (at least totals and accuracy).

---

### User Story 6 - Start activity from home (Priority: P3)

As a learner, I want “Minimal Pairs” to be discoverable from the home screen so I can quickly start listening practice.

**Why this priority**: Quick access increases usage and makes it feel like a first-class activity.

**Independent Test**: Can be tested by launching the app, navigating from home to the activity, and starting a session.

**Acceptance Scenarios**:

1. **Given** I am on the home screen, **When** I tap “Minimal Pairs”, **Then** I can select a pair and begin a session.

---

### Edge Cases

- Audio unavailable or fails to play (missing audio, interrupted playback, device muted).
- User rapidly taps answers or replays; input must not double-count a single prompt.
- User leaves the page mid-session (incoming call, app backgrounding, navigation away) and returns.
- Offline usage: activity should still function if required audio/content is already available; otherwise show a clear “needs download/connection” message.
- Accessibility: prompts and controls must be usable without relying on color alone; provide clear text/labels and large enough tap targets.
- Cross-platform UI: works on small screens (mobile) and large screens (desktop) without layout breakage.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a “Minimal Pairs” activity entry point from within the app’s learning activities.
- **FR-002**: System MUST allow selecting a target language (initial scope: Korean).
- **FR-003**: System MUST allow selecting a minimal pair to practice (two confusable options).
- **FR-004**: System MUST present a repeated sequence of trials where each trial:
  - Plays a single audio prompt
  - Shows exactly two answer choices
  - Accepts exactly one answer (unless user explicitly skips)
- **FR-005**: System MUST provide immediate feedback after each answer indicating correct vs incorrect.
- **FR-006**: System MUST show a running progress indicator during a session including at least: correct count, incorrect count, and accuracy percentage.
- **FR-007**: System MUST show an end-of-session summary including totals (correct/incorrect), accuracy percentage, and session duration.
- **FR-008**: System MUST persist session results so the user can view progress over time.
- **FR-009**: System MUST allow a practice mode selection:
  - Focus mode: practice a single selected pair until changed
  - Mixed mode: practice from a set of pairs in the same session
- **FR-010**: In mixed mode, the system MUST present more practice for items the user is missing more often.
- **FR-011**: System MUST support replaying the current prompt audio without advancing the trial.
- **FR-012**: System MUST work on the app’s supported mobile and desktop platforms.
- **FR-013**: System MUST support offline practice when required audio/content is already available, and MUST clearly communicate when content is not available offline.
- **FR-014**: All user-facing strings MUST be localizable.
- **FR-015**: System MUST provide a global speech-voice preference in the app’s Settings that is used as the default voice for audio generation in learning activities.
- **FR-016**: The Minimal Pairs activity MUST use the same audio playback interaction pattern as other audio-enabled experiences in the app (play, replay, and clear indication of playing state).
- **FR-017**: Any activity-specific “preferences sheets” for audio voice selection MUST be replaced by the global Settings preference where appropriate, so the voice choice is consistent across the app.
- **FR-018**: The “How do you say” experience MUST continue to allow in-page voice selection, but MUST start with the global voice preference as its default.
- **FR-019**: System MUST allow creating a minimal pair by associating exactly two existing vocabulary words.
- **FR-020**: System SHOULD allow an optional label describing the sound contrast being practiced for a minimal pair.
- **FR-021**: System MUST record attempts in a way that supports computing history and success rate per minimal pair.
- **FR-022**: Users MUST be able to view per-minimal-pair performance (correct, incorrect, accuracy) and a session history.
- **FR-023**: System MUST include “Minimal Pairs” as an activity entry point on the home screen.
- **FR-024**: The Minimal Pairs UI MUST follow the app’s established visual system (centralized styles and shared icons) and MUST NOT rely on text color alone for correctness feedback.
- **FR-025**: The vocabulary quiz preferences MUST be configurable from the centralized Settings experience rather than from an in-activity preferences sheet.
- **FR-026**: The vocabulary editing experience MUST use the global speech-voice preference as the default for any generated word audio.

#### Learning Approach (non-implementation guidance)

To support learning effectiveness, the default session behavior SHOULD combine:

- A short “warm-up” period with repeated exposure to the same pair (confidence-building, reduces task confusion)
- Followed by mixed/adaptive practice to improve discrimination under variable conditions

This balances “blocked practice” benefits for onboarding with “interleaving” benefits for durable learning and generalization.

### Key Entities *(include if feature involves data)*

- **Vocabulary Word**: A learner’s stored vocabulary item that can be associated to learning activities.
- **Minimal Pair**: A pair definition within a language that links exactly two vocabulary words and may include an optional “sound contrast” label.
- **Prompt**: A single trial item containing an audio clip and the correct answer mapping to one side of the minimal pair.
- **Session**: A user’s run of multiple prompts with start/end time and selected mode (focus/mixed).
- **Attempt**: One answered prompt within a session, including the user’s choice, correctness, and timestamp.
- **Progress Summary**: Aggregated stats computed from attempts (per session and over time), including correct/incorrect and accuracy.
- **Voice Preference**: A global setting indicating the default voice used for audio generation in learning activities.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user can start a minimal-pairs session and complete 20 trials in under 5 minutes.
- **SC-002**: During a session, users receive correctness feedback within 1 second of answering in at least 95% of trials.
- **SC-003**: Users can view a summary with correct, incorrect, and accuracy for every completed session.
- **SC-004**: For users who complete at least 5 sessions on the same minimal pair, median accuracy improves by at least 15 percentage points compared to their first session.
- **SC-005**: At least 90% of users who start the activity can complete one full session without external help.
- **SC-006**: A user can create a minimal pair from two vocabulary words in under 60 seconds.
- **SC-007**: For any minimal pair with at least one completed session, users can view per-pair correct, incorrect, and accuracy without manual calculation.
- **SC-008**: At least 80% of users can find and open “Minimal Pairs” from the home screen within 10 seconds of reaching home.

## Assumptions

- The product can provide access to short audio prompts representing each option in a minimal pair, using the same audio generation capabilities already used elsewhere in the app.
- Immediate feedback is beneficial for this sound-discrimination task and is required for the MVP.
- The default practice approach is a brief warm-up (blocked practice) followed by mixed/adaptive practice, to support both confidence-building and durable discrimination.
- A single global voice preference is acceptable as the default for learning activities, while some experiences may still offer an explicit in-page voice picker.

## Out of Scope (for this feature)

- Creating or sourcing new audio recordings as part of this feature.
- Deep phonetics instruction modules (e.g., full articulatory lessons) beyond minimal supportive hints.
- Multiplayer/competitive modes.

