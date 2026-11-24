namespace YuWangCloudSync.Net;

/// <summary>
/// Preparing → Uploading → Confirming (waiting for the directory entry) → Verifying (full
/// download and SHA-256 comparison, unless skipped by policy) → Finalizing.
/// </summary>
public enum UploadStage { Preparing, Uploading, Verifying, Finalizing, Confirming }

/// <summary>
/// Sent/confirmed counters include the server's resumed prefix. Subtract ResumedBytes for
/// this attempt's writes; only VerifiedBytes records content checked by a full download.
/// </summary>
public readonly record struct UploadProgress(UploadStage Stage, long TotalBytes,
    long SentBytes, long ConfirmedBytes, long VerifiedBytes = 0, long ResumedBytes = 0);
