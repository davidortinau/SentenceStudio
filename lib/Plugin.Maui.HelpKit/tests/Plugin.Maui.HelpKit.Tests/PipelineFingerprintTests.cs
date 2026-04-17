using Plugin.Maui.HelpKit.Rag;
using Xunit;

namespace Plugin.Maui.HelpKit.Tests;

public class PipelineFingerprintTests
{
    [Fact]
    public void Compute_ProducesDeterministicHash_ForSameInputs()
    {
        var a = PipelineFingerprint.Compute("text-embedding-3-small", 512, 128);
        var b = PipelineFingerprint.Compute("text-embedding-3-small", 512, 128);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA-256 hex
    }

    [Fact]
    public void Compute_IsCaseInsensitive_ForEmbeddingModelId()
    {
        var lower = PipelineFingerprint.Compute("text-embedding-3-small", 512, 128);
        var upper = PipelineFingerprint.Compute("TEXT-EMBEDDING-3-SMALL", 512, 128);

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Compute_ChangesWhenEmbeddingModelChanges()
    {
        var a = PipelineFingerprint.Compute("text-embedding-3-small", 512, 128);
        var b = PipelineFingerprint.Compute("text-embedding-3-large", 512, 128);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_ChangesWhenChunkSizeChanges()
    {
        var a = PipelineFingerprint.Compute("m", 512, 128);
        var b = PipelineFingerprint.Compute("m", 1024, 128);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_ChangesWhenOverlapChanges()
    {
        var a = PipelineFingerprint.Compute("m", 512, 128);
        var b = PipelineFingerprint.Compute("m", 512, 64);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_ChangesWhenChunkerVersionChanges()
    {
        var a = PipelineFingerprint.Compute("m", 512, 128, chunkerVersion: "v1");
        var b = PipelineFingerprint.Compute("m", 512, 128, chunkerVersion: "v2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_ChangesWhenHeadingFormatChanges()
    {
        var a = PipelineFingerprint.Compute("m", 512, 128, headingFormat: "breadcrumb");
        var b = PipelineFingerprint.Compute("m", 512, 128, headingFormat: "flat");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_ThrowsOnMissingEmbeddingModelId()
    {
        Assert.Throws<ArgumentException>(
            () => PipelineFingerprint.Compute(string.Empty, 512, 128));
        Assert.Throws<ArgumentException>(
            () => PipelineFingerprint.Compute("   ", 512, 128));
    }
}
