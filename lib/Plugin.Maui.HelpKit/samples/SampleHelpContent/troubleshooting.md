# Troubleshooting

## The help pane does not appear

Check these in order:

1. Confirm you called `builder.AddHelpKit(...)` in `MauiProgram.cs` before
   `builder.Build()`.
2. Confirm an `IHelpKitPresenter` is resolvable. The default selector picks
   `ShellPresenter` when Shell is present, `WindowPresenter` otherwise. If
   you call `ShowAsync()` before the first window is ready, the selector
   throws — wait for `Window.Created` or use `OnAppearing`.
3. For MauiReactor hosts, verify that `MauiControls.Shell.Current` is
   non-null when `ShowAsync()` fires. If not, register
   `MauiReactorPresenter` explicitly after `AddHelpKit()`.

## "No IChatClient registered" at runtime

HelpKit resolves AI services through `HelpKitAiResolver`, which tries keyed DI
first (`options.HelpKitServiceKey`, default `"helpkit"`), then falls back to
unkeyed registration. If neither is present, the resolver throws with a clear
message.

## Ingestion is not picking up my markdown

- Verify each path in `HelpKitOptions.ContentDirectories` is an absolute,
  readable filesystem path (MauiAssets must be copied out first — see
  `SampleHelpContentInstaller` for the pattern).
- If you changed the embedding model or chunking, delete the HelpKit storage
  directory (`{AppDataDirectory}/helpkit` by default) and restart.

## Rate limit hits during testing

Default is 10 questions per minute. Bump it via
`HelpKitOptions.MaxQuestionsPerMinute` for development, then lower it again
before release.
