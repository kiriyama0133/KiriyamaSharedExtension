using KiriyamaShareBackend.Shared.Sharing;

namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 一次快照入库的结果：存了什么、该告诉谁。
/// </summary>
public sealed record ShareSnapshotResult
{
    /// <summary>房间号。</summary>
    public string RoomCode { get; init; } = string.Empty;

    /// <summary>房间里的订阅者连接 id（不含房主）。</summary>
    public string[] SubscriberConnectionIds { get; init; } = [];

    /// <summary>服务端盖章后的快照（房间号、序号、截断标记、时间都由服务端定）。</summary>
    public DomSnapshotDto Snapshot { get; init; } = new();
}
