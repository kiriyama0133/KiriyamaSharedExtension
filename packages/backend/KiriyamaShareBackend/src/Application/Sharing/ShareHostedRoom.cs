using KiriyamaShareBackend.Shared.Sharing;

namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 某个连接正在共享的那个房间，用在"这次发起是不是换了共享目标"的判断上。
/// </summary>
public sealed record ShareHostedRoom
{
    /// <summary>房间快照（含当前的标题与地址）。</summary>
    public ShareRoomDto Room { get; init; } = new();

    /// <summary>房间里的订阅者连接 id（不含房主）。</summary>
    public string[] SubscriberConnectionIds { get; init; } = [];
}
