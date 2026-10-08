namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 服务端上的一个共享房间快照。
/// 字段与前端 <c>types/index.ts</c> 的 <c>SharedRoom</c> 一一对应。
/// </summary>
public class ShareRoomDto
{
    /// <summary>房间号。同时也是用户手动输入 / 分享出去的那个短码。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>被共享页面的标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>被共享页面的地址。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>发起方的显示名。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>房间当前人数，含发起方。</summary>
    public int PeerCount { get; set; }

    /// <summary>
    /// 往返延迟（毫秒）。服务端测不出客户端的 RTT，这里恒为 null，
    /// 由客户端 Ping 一次后自己填。
    /// </summary>
    public long? LatencyMs { get; set; }

    /// <summary>最后一次变更时间，epoch 毫秒（与前端 Date.now() 同一量纲）。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>该快照对"请求者本人"是否处于已订阅状态。</summary>
    public bool Subscribed { get; set; }
}
