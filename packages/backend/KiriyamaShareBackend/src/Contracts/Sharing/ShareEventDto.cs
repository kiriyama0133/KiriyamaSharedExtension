namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 房间生命周期事件。只给结构化信息，文案由客户端组织（服务端不做本地化文案）。
/// </summary>
public class ShareEventDto
{
    /// <summary>事件种类，取值见 <see cref="ShareEventKinds"/>。</summary>
    public string Kind { get; set; } = ShareEventKinds.System;

    /// <summary>房间号。</summary>
    public string RoomCode { get; set; } = string.Empty;

    /// <summary>加入 / 离开的那个人；房间关闭等事件为 null。</summary>
    public SharePeerDto? Peer { get; set; }

    /// <summary>
    /// 跳转事件（<see cref="ShareEventKinds.Navigate"/>）专用的新地址；其它事件为 null。
    ///
    /// 只给地址不给页面内容：内容走整页快照，那是另一条链路（快照可以到几百 KB，
    /// 塞进事件里会把活动流这种小消息撑爆）。
    /// </summary>
    public string? Url { get; set; }

    /// <summary>发生时间，epoch 毫秒。</summary>
    public long At { get; set; }
}
