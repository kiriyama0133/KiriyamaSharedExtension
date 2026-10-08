using KiriyamaShareBackend.Shared.Sharing;

namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 一次成员变化的结果：该通知谁、通知什么。
///
/// 之所以把"发给谁"也算出来，是因为 SignalR 的组（Group）在成员频繁进出时
/// 需要额外维护一套生命周期，而这里直接按连接 id 发就够了 —— 房间规模很小，
/// 少一层状态就少一类 bug。
/// </summary>
public sealed record ShareAudience
{
    /// <summary>房间号。</summary>
    public string RoomCode { get; init; } = string.Empty;

    /// <summary>接收方连接 id。房间关闭时是关闭前那一刻的成员。</summary>
    public string[] ConnectionIds { get; init; } = [];

    /// <summary>
    /// 触发这次变化的人。广播"谁进谁出"时排除它自己 —— 它已经知道结果了，
    /// 再收到一条"我加入了"只会让活动流很怪。
    /// </summary>
    public string? ActorConnectionId { get; init; }

    /// <summary>房间当前成员快照（含房主）。</summary>
    public SharePeerDto[] Members { get; init; } = [];

    /// <summary>本次加入 / 离开的人；房间关闭时为 null。</summary>
    public SharePeerDto? Peer { get; init; }

    /// <summary>房间是否已被关闭。</summary>
    public bool RoomClosed { get; init; }
}
