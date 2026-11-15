namespace SentenceStudio.Native.iOS.Features.LearningResources;

/// <summary>The open resource's study history (issue #219), read beside the resource.</summary>
internal sealed partial class LearningResourcesCoordinator
{
    private readonly NativeResourceStudyHistory? _studyHistory;

    /// <summary>The history the form shows; nothing while creating or before a read.</summary>
    private NativeResourceStudyState StudyState =>
        _studyHistory is { } history && _formOpen && !_creating
            && history.State.ResourceId == _selectedId
            ? history.State
            : new NativeResourceStudyState(NativeResourceStudyPhase.Idle);

    private Task LoadStudyHistoryAsync(string id) =>
        _studyHistory?.LoadAsync(id, _lifetime.Token) ?? Task.CompletedTask;

    /// <summary>The history's Retry after a failed read: reads it again and redraws.</summary>
    private async Task RetryStudyHistoryAsync()
    {
        if (!_formOpen || _creating || _selectedId is not { } id || _studyHistory is null)
            return;
        var formGeneration = Interlocked.Read(ref _formGeneration);
        await _studyHistory.LoadAsync(id, _lifetime.Token);
        if (IsCurrentForm(formGeneration, id))
            Publish();
    }
}
