using FluentAssertions;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services;

public sealed class RepositoryMutationOutcomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuickAddResult_ControlsSuccessAndPreservesStateOnRefusal(bool repositoryResult)
    {
        var outcome = RepositoryMutationOutcome.From(repositoryResult);

        outcome.ShouldShowSuccess.Should().Be(repositoryResult);
        outcome.ShouldNavigate.Should().Be(repositoryResult);
        outcome.PreservePageState.Should().Be(!repositoryResult);
    }

    [Fact]
    public void RefusedSaveAndDelete_DoNotNavigateOrReportSuccess()
    {
        var refusedSave = RepositoryMutationOutcome.From(string.Empty);
        var refusedDelete = RepositoryMutationOutcome.From(0);

        refusedSave.ShouldNavigate.Should().BeFalse();
        refusedSave.ShouldShowSuccess.Should().BeFalse();
        refusedSave.PreservePageState.Should().BeTrue();
        refusedDelete.ShouldNavigate.Should().BeFalse();
        refusedDelete.ShouldShowSuccess.Should().BeFalse();
        refusedDelete.PreservePageState.Should().BeTrue();
    }

    [Fact]
    public void RefusedBulkImport_RestoresOriginalVocabularyCollection()
    {
        var original = new VocabularyWord { Id = "existing", TargetLanguageTerm = "기존" };
        var resource = new LearningResource
        {
            Id = "resource",
            Vocabulary = [original]
        };

        using (var mutation = ResourceVocabularyMutation.Begin(resource))
        {
            resource.Vocabulary.Add(new VocabularyWord
            {
                Id = "candidate",
                TargetLanguageTerm = "추가"
            });

            var outcome = mutation.Accept(string.Empty);
            outcome.Succeeded.Should().BeFalse();
        }

        resource.Vocabulary.Should().ContainSingle()
            .Which.Id.Should().Be("existing");
    }

    [Fact]
    public void SuccessfulBulkImport_KeepsStagedVocabulary()
    {
        var resource = new LearningResource
        {
            Id = "resource",
            Vocabulary = []
        };

        using (var mutation = ResourceVocabularyMutation.Begin(resource))
        {
            resource.Vocabulary.Add(new VocabularyWord
            {
                Id = "candidate",
                TargetLanguageTerm = "추가"
            });

            mutation.Accept("resource").Succeeded.Should().BeTrue();
        }

        resource.Vocabulary.Should().ContainSingle()
            .Which.Id.Should().Be("candidate");
    }
}
