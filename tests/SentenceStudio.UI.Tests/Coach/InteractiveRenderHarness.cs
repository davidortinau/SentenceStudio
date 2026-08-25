using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Dispatcher = Microsoft.AspNetCore.Components.Dispatcher;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// A minimal interactive <see cref="Renderer"/> that can DISPATCH a real click into a Coach
/// component and observe whether the component's event handler throws.
/// </summary>
/// <remarks>
/// <para>
/// The existing render tests use <see cref="HtmlRenderer"/>, which produces static markup and
/// reports itself non-interactive. That is enough to assert on emitted HTML, but it cannot drive
/// the JS-interop paths behind the conversation shelf: those run only in response to a click, and
/// the focus paths are additionally gated on <c>RendererInfo.IsInteractive</c>. This renderer
/// reports an interactive server circuit and exposes a click-by-button-text primitive so a
/// regression can execute the exact handler that crashed in production.
/// </para>
/// <para>
/// Exceptions Blazor considers unhandled during event dispatch are routed to
/// <see cref="HandleException"/> — the same path the live circuit used when it logged
/// "Unhandled exception in circuit" and terminated. Tests assert on <see cref="Unhandled"/>.
/// </para>
/// </remarks>
internal sealed class InteractiveTestRenderer : Renderer
{
    private readonly List<Exception> _unhandled = new();

    public InteractiveTestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : base(services, loggerFactory)
    {
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    // The components under test call RendererInfo.IsInteractive before touching JS. Model an
    // interactive server circuit so those paths actually execute rather than being skipped.
    protected override RendererInfo RendererInfo { get; } = new RendererInfo("Server", isInteractive: true);

    /// <summary>Exceptions Blazor routed to the renderer instead of to the caller.</summary>
    public IReadOnlyList<Exception> Unhandled => _unhandled;

    protected override void HandleException(Exception exception) => _unhandled.Add(exception);

    /// <summary>
    /// Creates a component that declares its own render mode, the way a real circuit does.
    /// </summary>
    /// <remarks>
    /// The base renderer refuses every render mode, so a page carrying <c>@rendermode
    /// InteractiveServer</c> could not be hosted here at all. Inside an interactive server
    /// renderer that declaration is satisfied by the renderer itself, so the component is simply
    /// created — which is exactly what <c>RemoteRenderer</c> does. Any other mode still throws,
    /// because this renderer genuinely cannot host one.
    /// </remarks>
    protected override IComponent ResolveComponentForRenderMode(
        Type componentType,
        int? parentComponentId,
        IComponentActivator componentActivator,
        IComponentRenderMode renderMode) =>
        renderMode is InteractiveServerRenderMode
            ? componentActivator.CreateInstance(componentType)
            : throw new NotSupportedException(
                $"{nameof(InteractiveTestRenderer)} models an interactive server circuit and "
                + $"cannot host the render mode '{renderMode.GetType().Name}'.");

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

    /// <summary>Renders <typeparamref name="TComponent"/> as a root component and returns its id.</summary>
    public Task<int> RenderAsync<TComponent>() where TComponent : IComponent =>
        RenderAsync<TComponent>(ParameterView.Empty);

    public Task<int> RenderAsync<TComponent>(ParameterView parameters) where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            var component = InstantiateComponent(typeof(TComponent));
            var id = AssignRootComponentId(component);
            LastRootComponent = component;
            await RenderRootComponentAsync(id, parameters);
            return id;
        });

    /// <summary>
    /// Re-renders an existing root component with different parameters, keeping the instance.
    /// </summary>
    /// <remarks>
    /// This is what a positional rebind looks like from the component's point of view: the same
    /// object, the same local state, a different parameter. It is the only way to test that a
    /// control which holds interaction state survives being handed somebody else's identity —
    /// creating a second root would create a second instance and prove nothing.
    /// </remarks>
    public Task SetRootParametersAsync(int componentId, ParameterView parameters) =>
        Dispatcher.InvokeAsync(() => RenderRootComponentAsync(componentId, parameters));

    /// <summary>
    /// The most recently rendered root component instance, so a test can inspect the object that
    /// actually received (or correctly ignored) an event.
    /// </summary>
    public IComponent? LastRootComponent { get; private set; }

    /// <summary>
    /// Disposes a root component the way the framework does when the surrounding tree goes away.
    /// </summary>
    /// <remarks>
    /// On the MAUI BlazorWebView this happens on every full document load, because
    /// <c>WebViewManager.AttachToPageAsync</c> disposes the current <c>PageContext</c> before
    /// building the next one. Services registered scoped — the account boundary, the workspace —
    /// are still raising events while it happens.
    /// </remarks>
    public Task DisposeRootComponentAsync(int componentId) =>
        Dispatcher.InvokeAsync(() => RemoveRootComponent(componentId));

    /// <summary>
    /// Clicks the first <c>&lt;button&gt;</c> whose visible text contains <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// A missing button throws (that is a broken test, not a product signal). Any exception the
    /// handler produces is captured into <see cref="Unhandled"/> so a test can assert on it
    /// regardless of whether Blazor faults the dispatch task or routes it to HandleException.
    /// </remarks>
    public Task ClickButtonAsync(int componentId, string text) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var handlerId = FindButtonClickHandler(componentId, text)
                ?? throw new InvalidOperationException(
                    $"No button whose text contains '{text}' was found in component {componentId}.");

            try
            {
                await DispatchEventAsync(handlerId, fieldInfo: null, new MouseEventArgs());
            }
            catch (Exception ex)
            {
                _unhandled.Add(ex);
            }
        });

    /// <summary>
    /// Every <c>id</c> attribute rendered by a component and by everything it rendered.
    /// </summary>
    /// <remarks>
    /// Walks into child components rather than stopping at the component frame, because the
    /// elements a shell host is judged on — the FAB, the panel — are emitted by its children. A
    /// caller asserting "Sam is not on screen" needs the whole subtree, not the root's own frames.
    /// </remarks>
    public IReadOnlyList<string> RenderedElementIds(int componentId)
    {
        var ids = new List<string>();
        CollectElementIds(componentId, ids);
        return ids;
    }

    /// <summary>The component types instantiated beneath a component, in render order.</summary>
    public IReadOnlyList<string> RenderedComponentNames(int componentId)
    {
        var names = new List<string>();
        CollectComponentNames(componentId, names);
        return names;
    }

    /// <summary>
    /// All text and markup a component and its children rendered, concatenated.
    /// </summary>
    /// <remarks>
    /// Enough to assert on what a reader sees — a speaker label, a heading — without a full HTML
    /// renderer, which cannot be used here because the components under test only take their
    /// interactive paths when <c>RendererInfo.IsInteractive</c> is true.
    /// </remarks>
    public string RenderedText(int componentId)
    {
        var builder = new StringBuilder();
        CollectText(componentId, builder);
        return builder.ToString();
    }

    private void CollectText(int componentId, StringBuilder builder)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;

        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref array[i];

            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Text:
                    builder.Append(frame.TextContent).Append('\u001f');
                    break;
                case RenderTreeFrameType.Markup:
                    builder.Append(frame.MarkupContent).Append('\u001f');
                    break;
                case RenderTreeFrameType.Component when frame.ComponentId != 0:
                    CollectText(frame.ComponentId, builder);
                    break;
            }
        }
    }

    private void CollectElementIds(int componentId, List<string> ids)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;

        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref array[i];

            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Attribute
                    when string.Equals(frame.AttributeName, "id", StringComparison.Ordinal)
                         && frame.AttributeValue is string id:
                    ids.Add(id);
                    break;

                case RenderTreeFrameType.Component when frame.ComponentId != 0:
                    CollectElementIds(frame.ComponentId, ids);
                    break;
            }
        }
    }

    private void CollectComponentNames(int componentId, List<string> names)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;

        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref array[i];

            if (frame.FrameType == RenderTreeFrameType.Component && frame.ComponentId != 0)
            {
                names.Add(frame.ComponentType.Name);
                CollectComponentNames(frame.ComponentId, names);
            }
        }
    }

    /// <summary>
    /// The attribute names Blazor emits on the element carrying <paramref name="elementId"/>.
    /// </summary>
    /// <remarks>
    /// Event modifiers such as <c>@onkeydown:stopPropagation</c> are compiled into attributes with
    /// reserved names rather than into markup, so a static HTML assertion cannot see them. Reading
    /// the render tree is what makes "this handler stops the event from reaching document" a
    /// testable claim instead of a comment.
    /// </remarks>
    public IReadOnlyList<string> AttributesOfElementWithId(int componentId, string elementId)
    {
        var found = new List<string>();
        CollectAttributesForElement(componentId, elementId, found);
        return found;
    }

    private bool CollectAttributesForElement(int componentId, string elementId, List<string> into)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;

        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref array[i];

            if (frame.FrameType == RenderTreeFrameType.Component && frame.ComponentId != 0)
            {
                if (CollectAttributesForElement(frame.ComponentId, elementId, into))
                {
                    return true;
                }

                continue;
            }

            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            var names = new List<string>();
            var isMatch = false;

            for (var j = i + 1; j < frames.Count && array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                names.Add(array[j].AttributeName);

                if (string.Equals(array[j].AttributeName, "id", StringComparison.Ordinal)
                    && array[j].AttributeValue as string == elementId)
                {
                    isMatch = true;
                }
            }

            if (isMatch)
            {
                into.AddRange(names);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clicks the first <c>&lt;button&gt;</c> carrying <paramref name="elementId"/> as its id.
    /// </summary>
    /// <remarks>
    /// The panel's size controls are icon-only, so there is no visible text to address them by.
    /// The ids come from <c>SamElementIds</c>, which is also what the shipped markup uses, so a
    /// test and a learner are pressing the same control.
    /// </remarks>
    public Task ClickButtonByIdAsync(int componentId, string elementId) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var handlerId = FindClickHandlerById(componentId, elementId)
                ?? throw new InvalidOperationException(
                    $"No clickable element with id '{elementId}' was found in component {componentId}.");

            try
            {
                await DispatchEventAsync(handlerId, fieldInfo: null, new MouseEventArgs());
            }
            catch (Exception ex)
            {
                _unhandled.Add(ex);
            }
        });

    /// <summary>
    /// Presses a key on the element carrying <paramref name="elementId"/>.
    /// </summary>
    /// <remarks>
    /// Escape has to be pressed on a real element with a real handler, not simulated by calling the
    /// method behind it. The whole question this answers is which handler the key reaches — a test
    /// that invoked the handler directly would pass whether or not the markup was wired up at all.
    /// </remarks>
    public Task PressKeyByIdAsync(int componentId, string elementId, string key) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var handlerId = FindHandlerById(componentId, elementId, "onkeydown")
                ?? throw new InvalidOperationException(
                    $"No element with id '{elementId}' and an onkeydown handler was found in component {componentId}.");

            try
            {
                await DispatchEventAsync(handlerId, fieldInfo: null, new KeyboardEventArgs { Key = key });
            }
            catch (Exception ex)
            {
                _unhandled.Add(ex);
            }
        });

    /// <summary>
    /// The <c>class</c> values of every ancestor of the first element carrying
    /// <paramref name="className"/>, outermost first.
    /// </summary>
    /// <remarks>
    /// The rejected layout put the report form inside the actions row, where it was squeezed in
    /// beside Copy. "Is the panel present" cannot tell that apart from "is the panel in the right
    /// place", and neither can a text assertion — both pass either way. Ancestry is the only thing
    /// that actually distinguishes them, so it is what the test asserts on.
    /// </remarks>
    public IReadOnlyList<string> AncestorClassesOf(int componentId, string className)
    {
        var found = new List<string>();
        CollectAncestorClasses(componentId, className, new List<string>(), found, out var hit);
        return hit ? found : Array.Empty<string>();
    }

    private void CollectAncestorClasses(
        int componentId,
        string className,
        List<string> open,
        List<string> into,
        out bool hit)
    {
        hit = false;

        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;
        var count = frames.Count;
        var closes = new List<(int End, bool Counted)>();

        for (var i = 0; i < count; i++)
        {
            for (var c = closes.Count - 1; c >= 0; c--)
            {
                if (closes[c].End <= i)
                {
                    if (closes[c].Counted)
                    {
                        open.RemoveAt(open.Count - 1);
                    }

                    closes.RemoveAt(c);
                }
            }

            ref readonly var frame = ref array[i];

            if (frame.FrameType == RenderTreeFrameType.Component && frame.ComponentId != 0)
            {
                CollectAncestorClasses(frame.ComponentId, className, open, into, out hit);

                if (hit)
                {
                    return;
                }

                continue;
            }

            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            var classValue = string.Empty;

            for (var j = i + 1; j < count && array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                if (string.Equals(array[j].AttributeName, "class", StringComparison.Ordinal))
                {
                    classValue = array[j].AttributeValue as string ?? string.Empty;
                }
            }

            if (HasClass(classValue, className))
            {
                into.AddRange(open);
                hit = true;
                return;
            }

            var end = Math.Min(i + frame.ElementSubtreeLength, count);
            var counted = classValue.Length > 0;

            if (counted)
            {
                open.Add(classValue);
            }

            closes.Add((end, counted));
        }

        for (var c = closes.Count - 1; c >= 0; c--)
        {
            if (closes[c].Counted)
            {
                open.RemoveAt(open.Count - 1);
            }
        }
    }

    private static bool HasClass(string classValue, string className)
    {
        foreach (var token in classValue.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, className, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>class</c> values of every element beneath the component, in render order.
    /// </summary>
    /// <remarks>
    /// Order is the point. The panel has to come after the actions row, not before it: a form that
    /// renders above the buttons that opened it moves those buttons under the reader's pointer at
    /// the moment they are most likely to press again.
    /// </remarks>
    public IReadOnlyList<string> RenderedClassesInOrder(int componentId)
    {
        var into = new List<string>();
        CollectClassesInOrder(componentId, into);
        return into;
    }

    private void CollectClassesInOrder(int componentId, List<string> into)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;

        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref array[i];

            if (frame.FrameType == RenderTreeFrameType.Component && frame.ComponentId != 0)
            {
                CollectClassesInOrder(frame.ComponentId, into);
                continue;
            }

            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            for (var j = i + 1; j < frames.Count && array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                if (string.Equals(array[j].AttributeName, "class", StringComparison.Ordinal)
                    && array[j].AttributeValue is string value
                    && value.Length > 0)
                {
                    into.Add(value);
                }
            }
        }
    }

    /// <summary>True when an element with this id is present anywhere beneath the component.</summary>
    public bool HasElementWithId(int componentId, string elementId) =>
        RenderedElementIds(componentId).Contains(elementId, StringComparer.Ordinal);

    /// <summary>
    /// Changes the value of the input carrying <paramref name="elementId"/>.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="ClickButtonByIdAsync"/> for a radio or a select: a closed
    /// choice is made by changing a value, not by clicking a button, and a test that asserted only
    /// on the default choice would never notice a group whose other options were wired to nothing.
    /// </remarks>
    public Task ChangeValueByIdAsync(int componentId, string elementId, object? value) =>
        Dispatcher.InvokeAsync(async () =>
        {
            var handlerId = FindHandlerById(componentId, elementId, "onchange")
                ?? throw new InvalidOperationException(
                    $"No element with id '{elementId}' and an onchange handler was found in component {componentId}.");

            try
            {
                await DispatchEventAsync(handlerId, fieldInfo: null, new ChangeEventArgs { Value = value });
            }
            catch (Exception ex)
            {
                _unhandled.Add(ex);
            }
        });

    /// <summary>
    /// The value of an attribute on the element carrying <paramref name="elementId"/>, or null.
    /// </summary>
    public string? AttributeValue(int componentId, string elementId, string attributeName)
    {
        string? found = null;
        CollectAttributeValue(componentId, elementId, attributeName, ref found);
        return found;
    }

    private bool CollectAttributeValue(
        int componentId, string elementId, string attributeName, ref string? into)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;

        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var frame = ref array[i];

            if (frame.FrameType == RenderTreeFrameType.Component && frame.ComponentId != 0)
            {
                if (CollectAttributeValue(frame.ComponentId, elementId, attributeName, ref into))
                {
                    return true;
                }

                continue;
            }

            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            var isMatch = false;
            string? value = null;

            for (var j = i + 1; j < frames.Count && array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                if (string.Equals(array[j].AttributeName, "id", StringComparison.Ordinal)
                    && array[j].AttributeValue as string == elementId)
                {
                    isMatch = true;
                }

                if (string.Equals(array[j].AttributeName, attributeName, StringComparison.Ordinal))
                {
                    value = array[j].AttributeValue as string;
                }
            }

            if (isMatch)
            {
                into = value;
                return true;
            }
        }

        return false;
    }

    private ulong? FindClickHandlerById(int componentId, string elementId) =>
        FindHandlerById(componentId, elementId, "onclick");

    /// <summary>
    /// The handler id for one named event on the element carrying <paramref name="elementId"/>.
    /// </summary>
    /// <remarks>
    /// Generalized from the click lookup rather than copied, so a click and a change are found by
    /// exactly the same walk. Two lookups that agree by accident are two lookups that will
    /// eventually disagree.
    /// </remarks>
    private ulong? FindHandlerById(int componentId, string elementId, string eventName)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;
        var count = frames.Count;

        for (var i = 0; i < count; i++)
        {
            ref readonly var frame = ref array[i];

            if (frame.FrameType == RenderTreeFrameType.Component && frame.ComponentId != 0)
            {
                var nested = FindHandlerById(frame.ComponentId, elementId, eventName);
                if (nested is not null)
                {
                    return nested;
                }

                continue;
            }

            if (frame.FrameType != RenderTreeFrameType.Element)
            {
                continue;
            }

            ulong handler = 0;
            var isMatch = false;

            for (var j = i + 1; j < count && array[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                ref readonly var attribute = ref array[j];

                if (string.Equals(attribute.AttributeName, "id", StringComparison.Ordinal)
                    && attribute.AttributeValue as string == elementId)
                {
                    isMatch = true;
                }

                if (string.Equals(attribute.AttributeName, eventName, StringComparison.Ordinal)
                    && attribute.AttributeEventHandlerId != 0)
                {
                    handler = attribute.AttributeEventHandlerId;
                }
            }

            if (isMatch && handler != 0)
            {
                return handler;
            }
        }

        return null;
    }

    private ulong? FindButtonClickHandler(int componentId, string text)    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var array = frames.Array;
        var count = frames.Count;

        for (var i = 0; i < count; i++)
        {
            ref readonly var frame = ref array[i];
            if (frame.FrameType != RenderTreeFrameType.Element ||
                !string.Equals(frame.ElementName, "button", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = Math.Min(i + frame.ElementSubtreeLength, count);
            ulong onClick = 0;
            var content = new StringBuilder();

            for (var j = i + 1; j < end; j++)
            {
                ref readonly var inner = ref array[j];
                switch (inner.FrameType)
                {
                    case RenderTreeFrameType.Attribute:
                        if (string.Equals(inner.AttributeName, "onclick", StringComparison.Ordinal) &&
                            inner.AttributeEventHandlerId != 0)
                        {
                            onClick = inner.AttributeEventHandlerId;
                        }
                        break;
                    case RenderTreeFrameType.Text:
                        content.Append(inner.TextContent);
                        break;
                    case RenderTreeFrameType.Markup:
                        content.Append(inner.MarkupContent);
                        break;
                }
            }

            if (onClick != 0 && content.ToString().Contains(text, StringComparison.Ordinal))
            {
                return onClick;
            }
        }

        return null;
    }
}

/// <summary>
/// A JS runtime double that models Blazor's real global-vs-module resolution, so a test can tell
/// a correct <c>import(...).InvokeVoidAsync(name)</c> call apart from the bug where a component
/// invokes an app.js export on the default runtime.
/// </summary>
/// <remarks>
/// The functions <c>focusElement</c>, <c>downloadFileFromStream</c> and <c>restoreScrollAnchor</c>
/// are ES-module exports of <c>app.js</c>; they are not attached to <c>window</c>. The browser
/// therefore throws <c>JSException: The value '&lt;name&gt;' is not a function</c> when they are
/// invoked through the default <see cref="IJSRuntime"/>, and returns normally when they are
/// invoked on the imported module reference. This double reproduces both behaviours exactly.
/// </remarks>
internal sealed class ModuleAwareJSRuntime : IJSRuntime
{
    private static readonly HashSet<string> ModuleOnlyExports = new(StringComparer.Ordinal)
    {
        "focusElement",
        "downloadFileFromStream",
        "restoreScrollAnchor"
    };

    /// <summary>Identifiers invoked on the default (global) runtime, in order.</summary>
    public List<string> GlobalInvocations { get; } = new();

    /// <summary>Identifiers invoked on an imported module, in order.</summary>
    public List<string> ModuleInvocations { get; } = new();

    /// <summary>Paths passed to <c>import()</c> calls, in order.</summary>
    public List<string> ImportedPaths { get; } = new();

    /// <summary>Arguments passed to the most recent module invocation.</summary>
    public object?[]? LastModuleArgs { get; private set; }

    /// <summary>
    /// Every module invocation with its arguments, in order.
    /// </summary>
    /// <remarks>
    /// <see cref="LastModuleArgs"/> only remembers the newest call, which is no use for a sequence
    /// like "focus the settled control, then close the scroll bracket": by the time the test looks,
    /// the arguments belong to the bracket. This keeps the whole transcript so a test can assert
    /// both what was called and in what order.
    /// </remarks>
    public List<(string Identifier, object?[]? Args)> ModuleCalls { get; } = new();

    /// <summary>The first argument of the first call to <paramref name="identifier"/>, if any.</summary>
    public object? FirstArgOf(string identifier) => ModuleCalls
        .Where(c => c.Identifier == identifier)
        .Select(c => c.Args?.FirstOrDefault())
        .FirstOrDefault();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        if (identifier == "import")
        {
            var path = args?.FirstOrDefault() as string ?? "";
            ImportedPaths.Add(path);
            return ValueTask.FromResult((TValue)(object)new Module(this));
        }

        GlobalInvocations.Add(identifier);

        if (ModuleOnlyExports.Contains(identifier))
        {
            // Exactly what the browser reports, and what the live circuit logged before dying.
            return ValueTask.FromException<TValue>(
                new JSException($"The value '{identifier}' is not a function."));
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    private sealed class Module(ModuleAwareJSRuntime owner) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            owner.ModuleInvocations.Add(identifier);
            owner.ModuleCalls.Add((identifier, args));
            owner.LastModuleArgs = args;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
