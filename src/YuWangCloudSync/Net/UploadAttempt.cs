namespace YuWangCloudSync.Net;

/// <summary>
/// Durable evidence for one upload: the directory before it began, whether a request may
/// already have published a file, and the observed candidate ID (not yet content-verified).
/// No token or other session secret is persisted.
/// </summary>
public sealed record UploadAttempt(ulong[] BeforeIds, bool MayHaveCommitted, ulong CandidateId = 0);
