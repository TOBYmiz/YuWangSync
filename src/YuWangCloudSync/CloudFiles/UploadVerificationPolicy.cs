using Windows.Networking.Connectivity;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// Runtime switch for the post-upload download verification. The client still verifies
/// whenever identity is uncertain (resumed prefix, crash recovery, same-name candidate);
/// this policy only decides whether a fully sent and acknowledged upload is re-downloaded.
/// </summary>
internal static class UploadVerificationPolicy
{
    private static volatile bool _enabled = true;
    private static volatile bool _onMetered;

    public static void Configure(bool enabled, bool onMeteredNetwork)
    {
        _enabled = enabled;
        _onMetered = onMeteredNetwork;
    }

    /// <summary>Evaluate at the start of each upload; the network can change between files.</summary>
    public static bool ShouldVerify(Action<string>? log = null)
    {
        if (!_enabled) return false;
        if (_onMetered || !IsMeteredNetwork()) return true;
        log?.Invoke("[UPLOAD] 当前为计费网络，按设置跳过上传后下载校验");
        return false;
    }

    /// <summary>Unknown cost is treated as unmetered so verification stays on by default.</summary>
    public static bool IsMeteredNetwork()
    {
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            if (cost is null) return false;
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                || cost.Roaming || cost.OverDataLimit;
        }
        catch (Exception) { return false; }
    }
}
