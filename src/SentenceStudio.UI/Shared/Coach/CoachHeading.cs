using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace SentenceStudio.WebUI.Shared.Coach;

/// <summary>
/// Renders a heading at a caller-chosen level.
/// </summary>
/// <remarks>
/// The coach components are shared by two presentations with different document outlines. In
/// the overlay the dialog title is an <c>h2</c>, so the plan canvas sections are <c>h3</c>. On
/// the <c>/coach</c> route PageHeader owns the <c>h1</c> and there is no dialog title, so those
/// same sections must be <c>h2</c> or the outline skips a level (WCAG 1.3.1 / 2.4.6).
///
/// Razor cannot take a dynamic element name, so this is a component rather than markup.
/// </remarks>
public sealed class CoachHeading : ComponentBase
{
    /// <summary>Heading level, 1-6. Values outside that range are clamped.</summary>
    [Parameter]
    public int Level { get; set; } = 2;

    /// <summary>CSS classes applied to the heading element.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Optional DOM id, for aria-labelledby references.</summary>
    [Parameter]
    public string? Id { get; set; }

    /// <summary>Optional tabindex, for headings that receive programmatic focus.</summary>
    [Parameter]
    public int? TabIndex { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var level = Math.Clamp(Level, 1, 6);

        builder.OpenElement(0, $"h{level}");

        if (!string.IsNullOrWhiteSpace(Id))
        {
            builder.AddAttribute(1, "id", Id);
        }

        if (!string.IsNullOrWhiteSpace(Class))
        {
            builder.AddAttribute(2, "class", Class);
        }

        if (TabIndex.HasValue)
        {
            builder.AddAttribute(3, "tabindex", TabIndex.Value);
        }

        builder.AddContent(4, ChildContent);
        builder.CloseElement();
    }
}
