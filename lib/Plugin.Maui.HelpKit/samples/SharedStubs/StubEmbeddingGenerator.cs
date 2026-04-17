using Microsoft.Extensions.AI;

namespace HelpKitSample.SharedStubs;

/// <summary>
/// A deterministic fake <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> that
/// hashes each input string into a fixed-dimension float vector. Produces stable
/// output across runs so ingestion fingerprints remain consistent, which keeps
/// the HelpKit RAG layer happy during offline sample runs.
///
/// This is NOT a real embedding model — semantic search quality is effectively
/// random. Swap in a real Microsoft.Extensions.AI embedding generator in
/// production (see the comment block in each sample's <c>MauiProgram.cs</c>).
/// </summary>
public sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 32;

    public EmbeddingGeneratorMetadata Metadata { get; } =
        new("stub-embedding-generator", new Uri("https://example.invalid/stub"), "stub-embed", Dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Add(new Embedding<float>(HashToVector(value ?? string.Empty)));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IEmbeddingGenerator<string, Embedding<float>>) ? this : null;

    public void Dispose() { }

    private static float[] HashToVector(string input)
    {
        var vector = new float[Dimensions];
        unchecked
        {
            uint seed = 2166136261u;
            foreach (var c in input)
                seed = (seed ^ c) * 16777619u;

            for (var i = 0; i < Dimensions; i++)
            {
                seed = (seed ^ (uint)(i * 2654435761u)) * 16777619u;
                vector[i] = ((seed % 20000u) / 10000f) - 1f;
            }
        }

        double magnitudeSquared = 0;
        for (var i = 0; i < Dimensions; i++)
            magnitudeSquared += vector[i] * vector[i];

        var magnitude = (float)Math.Sqrt(magnitudeSquared);
        if (magnitude > 0f)
        {
            for (var i = 0; i < Dimensions; i++)
                vector[i] /= magnitude;
        }

        return vector;
    }
}
