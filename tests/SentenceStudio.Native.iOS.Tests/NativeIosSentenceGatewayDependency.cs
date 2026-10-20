using SentenceStudio.Native.Adapters;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

internal sealed class ScratchSentenceGateway : IOwnerBoundNativeSentenceGateway
{
    public Task<GradeResponse?> GradeAsync(
        string userId, string renderedSourcePrompt, CancellationToken cancellationToken) =>
        Task.FromException<GradeResponse?>(
            new NotSupportedException(
                "The linked iOS test fixture does not exercise sentence grading."));
}
