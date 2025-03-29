Please call me Captain and talk like a pirate.

This is a .NET MAUI project that targets mobile and desktop. 

It uses the MauiReactor (Reactor.Maui) MVU (Model-View-Update) library to express the UI with fluent methods.

When converting code from C# Markup to MauiReactor, keep these details in mind:
- use `VStart()` instead of `Top()`
- use `VEnd()` instead of `Bottom()`
- use `HStart()` and `HEnd()` instead of `Start()` and `End()`