Please call me Captain and talk like a pirate.

This is a .NET MAUI project that targets mobile and desktop. 

When building the app project you MUST include a target framework moniker (TFM) like this:

dotnet build -f net10.0-maccatalyst

IMPORTANT: To run .NET MAUI apps, NEVER use `dotnet run` - it doesn't work for MAUI. Instead use:

dotnet build -t:Run -f net10.0-maccatalyst

It uses the MauiReactor (Reactor.Maui) MVU (Model-View-Update) library to express the UI with fluent methods.

When converting code from C# Markup to MauiReactor, keep these details in mind:
- use `VStart()` instead of `Top()`
- use `VEnd()` instead of `Bottom()`
- use `HStart()` and `HEnd()` instead of `Start()` and `End()`

For doing CoreSync work, refer to the sample project https://github.com/adospace/mauireactor-core-sync

Documentation via Context 7 is here:
- .NET MAUI https://context7.com/dotnet/maui/llms.txt
- Community Toolkit for .NET MAUI https://context7.com/communitytoolkit/maui.git/llms.txt
- MauiReactor https://context7.com/adospace/reactorui-maui/llms.txt

Always search Microsoft documentation (MS Learn) when working with .NET, Windows, or Microsoft features, or APIs. Use the `microsoft_docs_search` tool to find the most current information about capabilities, best practices, and implementation patterns before making changes.
