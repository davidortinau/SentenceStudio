using MauiReactor;

namespace SentenceStudio;

public partial class SentenceStudioApp : Component
{
    protected override void OnMounted()
    {
        base.OnMounted();
    }

    public override VisualNode Render()
    {
        return new AppShell();
    }
}
