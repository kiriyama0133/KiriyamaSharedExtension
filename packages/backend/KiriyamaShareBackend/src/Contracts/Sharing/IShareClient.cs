namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 服务端 → 客户端的方法契约。
/// Hub 继承 <c>Hub&lt;IShareClient&gt;</c> 之后，服务端不用再手写字符串方法名，
/// 改名/加参数会在编译期暴露出来。
/// </summary>
public interface IShareClient
{
    /// <summary>
    /// 房间列表发生了变化（有人开始/结束共享）。
    /// 只报版本号和房间数，不带列表本身：<see cref="ShareRoomDto.Subscribed"/> 是按客户端
    /// 各算各的，逐个推送完整列表既浪费又不准；客户端收到后自己再调一次 ListRooms。
    /// </summary>
    /// <param name="version">单调递增的列表版本号。</param>
    /// <param name="roomCount">当前房间总数。</param>
    Task RoomsChanged(long version, int roomCount);

    /// <summary>
    /// 房间成员变化（有人进出）。带上完整成员列表，客户端整体替换即可，不用自己增删。
    /// </summary>
    /// <param name="roomCode">房间号。</param>
    /// <param name="peers">房间当前成员（含房主）。</param>
    Task PeersChanged(string roomCode, SharePeerDto[] peers);

    /// <summary>
    /// 订阅的房间里来了一个新的 patch（基于共同基线的增量）。发起方自己不会收到这条。
    ///
    /// 接收方**必须**先看 <see cref="DomPatchDto.BaseVersion"/> 是否等于自己的当前版本：
    /// 不等于就说明中间缺了东西，这时要丢弃这条并重新要一份基线，绝不能硬应用 ——
    /// 在错的基线上应用带下标的操作会静默地改错位置。
    /// </summary>
    /// <param name="patch">一条基于已知版本的增量。</param>
    Task ChangesReceived(DomPatchDto patch);

    /// <summary>
    /// 房间生命周期事件：有人加入 / 有人离开 / 房间被关闭。用来喂客户端的活动流。
    /// 只给结构化信息，不给文案——文案由客户端自己组织。
    /// </summary>
    /// <param name="shareEvent">事件内容。</param>
    Task ShareEventReceived(ShareEventDto shareEvent);

    /// <summary>
    /// 请房主交一份当前页面的整页基线。只发给房间的房主。
    ///
    /// 这是必需的：增量只有在接收方手里已经有同一份基线之后才有意义，而基线只有房主有。
    /// SignalR 没有服务端主动"问"客户端的机制，所以只能反过来由服务端发这条消息、
    /// 房主收到后调用 <c>PublishSnapshot</c> 交上来。
    /// </summary>
    /// <param name="roomCode">房间号。</param>
    Task SnapshotRequested(string roomCode);

    /// <summary>
    /// 房主刚交上来一份新基线，让订阅者去取。只发给房间的订阅者（房主自己不用）。
    /// 带上版本是为了让客户端能丢掉"比它现在该有的更旧"的那次响应。
    /// </summary>
    /// <param name="roomCode">房间号。</param>
    /// <param name="version">这份基线代表的房间版本。</param>
    Task SnapshotReady(string roomCode, long version);
}
