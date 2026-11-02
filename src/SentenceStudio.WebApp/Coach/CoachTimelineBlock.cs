using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.WebApp.Coach;

public sealed class CoachTimelineContent(CoachTimelineEntry entry) : AIContent
{
    public CoachTimelineEntry Entry { get; } = entry;
}

public sealed class CoachTimelineBlock : ActivityContentBlock
{
    public CoachTimelineEntry Entry { get; set; } = default!;
}

public sealed class CoachTimelineBlockHandler : ActivityHandler<CoachTimelineBlock>
{
    protected override bool TryCreateBlock(BlockMappingContext context, CoachTimelineBlock state)
    {
        foreach (var content in context.UnhandledContents)
        {
            if (content is CoachTimelineContent timeline)
            {
                context.MarkHandled(content);
                state.ActivityType = "coach-timeline";
                state.Entry = timeline.Entry;
                return true;
            }
        }
        return false;
    }

    protected override bool TryUpdateBlock(
        BlockMappingContext context, CoachTimelineBlock state, out bool isComplete)
    {
        isComplete = true;
        return false;
    }
}
