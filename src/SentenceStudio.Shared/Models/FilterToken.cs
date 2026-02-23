namespace SentenceStudio.Shared.Models;

public sealed record FilterToken(string Type, string Value)
{
    public static readonly IReadOnlyCollection<string> ValidTypes = new[]
    {
        "tag",
        "resource",
        "lemma",
        "status"
    };

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Type) &&
        !string.IsNullOrWhiteSpace(Value) &&
        ValidTypes.Contains(Type.ToLowerInvariant());
}
