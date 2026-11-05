# Sam entry: placement and hide on scroll

Captain, 2026-11-04. Applies to every client: web, MAUI (through the shared Razor UI),
native iOS, Android and macOS, and the future Flutter port.

## Where the entry is

- **Floating button** on the Dashboard (and Today's Plan where it is its own screen) and
  on Resources. It is round and shows only Sam's icon; it has no text label. Its
  accessible name is `Coach_OpenNamed` ("Ask {coach}").
- **Top-bar icon** on every other signed-in screen: activities, the quiz, Settings,
  Profile, Skills, Vocabulary, Recent lookups, Activity Log, forms. The web puts it in
  the shared `PageHeader`.
- No entry while signed out, onboarding, during the first sync, when Sam is unavailable
  for the owner, or while Sam's panel is open.

Docking the button at the screen edge was considered and dropped: with the rules below
it is not needed.

## The floating button never covers content

1. **Room at the bottom.** A screen with the floating button leaves space after its
   last item: the button's size plus its margin. The last card or row can always be
   scrolled clear of the button.
2. **Hide on scroll.** Shared rule: `src/SentenceStudio.Native/Sam/NativeSamEntryAutoHide.cs`.
   - Scrolling down 24 (points, dp or CSS pixels) in one direction slides the button
     away. Scrolling up 24 brings it back. Changing direction starts the count again.
   - At the top or the end of the screen it is always shown. A screen that cannot
     scroll counts as both.
   - A new screen or tab starts with it shown.
3. **Keyboard.** While the on-screen keyboard is up the button is hidden. The web, which
   cannot see the keyboard, hides it while a text field has focus on a touch device.
4. **Toasts and snackbars** appear above the button, never under or over it.

## Accessibility

- Native: while VoiceOver or TalkBack runs, scrolling does not hide the button.
- Web: a hidden button stays in the accessibility tree; when it receives focus it comes
  back.
- With reduced motion the button fades or switches instead of sliding.
- A hidden button cannot be tapped.

## Motion

The button slides down off the bottom edge and fades, in about 200 ms, and returns the
same way.
