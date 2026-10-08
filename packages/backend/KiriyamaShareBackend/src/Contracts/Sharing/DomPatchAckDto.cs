namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 房主推 patch 之后收到的回执。与前端 <c>types/index.ts</c> 的 <c>PatchAck</c> 对应。
///
/// 为什么不是简单的 bool：房主要靠回执做两件事 —— 推进自己的 base 版本，
/// 以及在被拒时判断该"重建基线"还是"干脆别共享了"。只有 bool 的话它分不出来。
/// </summary>
public class DomPatchAckDto
{
    /// <summary>这条 patch 有没有被接受并转发出去。</summary>
    public bool Accepted { get; set; }

    /// <summary>回执给出时房间的当前版本。被接受时是刚分配的新版本，被拒时是需要对齐到的版本。</summary>
    public long Version { get; set; }

    /// <summary>
    /// 没被接受的原因，见 <see cref="PatchRejectReasons"/>。被接受时是空串。
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>patch 被拒的原因。</summary>
public static class PatchRejectReasons
{
    /// <summary>
    /// <c>baseVersion</c> 和房间的当前版本对不上：中间丢了 patch，或者发送方的状态已经分叉。
    /// 处理方式：重新交一份整页基线对齐。
    /// </summary>
    public const string StaleBase = "stale-base";

    /// <summary>
    /// 发送方现在不是任何房间的房主（房间已经关了、或者本来就没开）。
    /// 处理方式：清掉本地的房间状态，别再往一个不存在的房间推东西。
    /// </summary>
    public const string NotHosting = "not-hosting";

    /// <summary>
    /// 这条 patch 超出了服务端能**原样**转发的规模（操作条数 / 单棵子树大小 / 单个值长度）。
    ///
    /// 服务端不会"裁一点再转发"：一旦裁了，接收方拿到的就不是发送方算出来的那组操作，
    /// 两端的树从这一刻起**永久分叉**，而且两边都以为自己是同步的。
    /// 宁可拒收：发送方收到后拆小重发，或者重新交一份基线对齐。
    ///
    /// 发送方本可以避免走到这里 —— 采集端是按条数上限**提前冲出去**的（分多次发），
    /// 不是攒够了丢掉多余的。
    /// </summary>
    public const string TooLarge = "too-large";
}
