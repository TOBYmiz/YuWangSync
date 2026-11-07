namespace YuWangCloudSync.Net;

public enum UploadStage { Preparing, Uploading, Verifying, Finalizing }

/// <summary>
/// Sent/confirmed counters include the server's resumed prefix. Subtract ResumedBytes for
/// this attempt's writes; only VerifiedBytes records content checked by a full download.
/// </summary>
public readonly record struct UploadProgress(UploadStage Stage, long TotalBytes,
    long SentBytes, long ConfirmedBytes, long VerifiedBytes = 0, long ResumedBytes = 0);
