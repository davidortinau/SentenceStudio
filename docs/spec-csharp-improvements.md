# Improving C# and Markup Extensions

While moving my UI from XAML to C#, I found a few unpleasant things that I would love to see improved.

1. Most common runtime exception I had to chase down was 'Specified cast is invalid`. This was almost always resource related.

```csharp
(Thickness)Application.Current.Resources["DarkOnLightBackground"]
```

Could this be caught at compile time? Could we introduce a better API for resources that avoids the need to cast?

2. Using XAML resources, Resource Dictionary

My code is littered with these calls to local and app level resources.

```csharp
(Color)Application.Current.Resources["DarkOnLightBackground"]
```

I like being able to define resources and styles in XAML, and use them in C#. It's also helpful for mixing UI modes and sharing across projects. It would be nice to have a strongly typed API to get my resources, akin Android res.

```csharp
var color = Application.Current.Resources.DarkOnLightBackground;
```

Could we code gen that?