namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 一次"请房主交快照"的解析结果：该问谁、顺带该通知哪些订阅者。
/// </summary>
public sealed record ShareSnapshotRequest
{
    /// <summary>房间号。</summary>
    public string RoomCode { get; init; } = string.Empty;

    /// <summary>房主的连接 id（快照只有它交得出来）。</summary>
    public string HostConnectionId { get; init; } = string.Empty;

    /// <summary>房间里的订阅者连接 id（不含房主）。快照落库后由它们去取。</summary>
    public string[] SubscriberConnectionIds { get; init; } = [];
}
