namespace SentenceStudio.Contracts.Startup;

public static class ApiApplicationStartupContract
{
    // v3 requires continuous backend attestation through identity and migration-history reads.
    // A v2 API must not release newer Workers or WebApp instances.
    // The fingerprint distinguishes different cluster identifiers, not physical clones.
    public const string Current = "api-exclusive-application-startup-v3";
}
