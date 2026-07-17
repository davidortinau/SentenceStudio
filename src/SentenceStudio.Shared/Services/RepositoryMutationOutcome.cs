using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

public readonly record struct RepositoryMutationOutcome(bool Succeeded)
{
    public bool ShouldShowSuccess => Succeeded;
    public bool ShouldNavigate => Succeeded;
    public bool PreservePageState => !Succeeded;

    public static RepositoryMutationOutcome From(bool result) => new(result);
    public static RepositoryMutationOutcome From(int result) => new(result > 0);
    public static RepositoryMutationOutcome From(string? result) =>
        new(!string.IsNullOrWhiteSpace(result));
}

/// <summary>
/// Stages vocabulary-list UI mutations and restores the original collection when
/// the repository refuses the aggregate save or an exception interrupts it.
/// </summary>
public sealed class ResourceVocabularyMutation : IDisposable
{
    private readonly LearningResource _resource;
    private readonly List<VocabularyWord>? _originalVocabulary;
    private bool _accepted;

    private ResourceVocabularyMutation(LearningResource resource)
    {
        _resource = resource;
        _originalVocabulary = resource.Vocabulary?.ToList();
    }

    public static ResourceVocabularyMutation Begin(LearningResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new ResourceVocabularyMutation(resource);
    }

    public RepositoryMutationOutcome Accept(string? repositoryResult)
    {
        var outcome = RepositoryMutationOutcome.From(repositoryResult);
        if (outcome.Succeeded)
            _accepted = true;
        else
            Restore();
        return outcome;
    }

    public void Dispose()
    {
        if (!_accepted)
            Restore();
    }

    private void Restore()
    {
        _resource.Vocabulary = _originalVocabulary?.ToList() ?? new List<VocabularyWord>();
    }
}
