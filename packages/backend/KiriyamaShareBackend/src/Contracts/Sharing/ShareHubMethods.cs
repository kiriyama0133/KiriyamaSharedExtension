namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 客户端 → 服务端的 Hub 方法名。
/// 服务端 → 客户端的方法名不在这里，直接由 <see cref="IShareClient"/> 提供（强类型）。
/// SignalR 匹配方法名时大小写不敏感，TS 客户端写 <c>publishChanges</c> 或
/// <c>PublishChanges</c> 都能落到同一个方法上。
/// </summary>
public static class ShareHubMethods
{
    /// <summary>拉取当前所有共享房间（每个房间的 subscribed 是按调用者算的）。</summary>
    public const string ListRooms = nameof(ListRooms);

    /// <summary>发起共享：把当前页面作为共享目标交给服务端。</summary>
    public const string StartSharing = nameof(StartSharing);

    /// <summary>结束共享，房间随即关闭。</summary>
    public const string StopSharing = nameof(StopSharing);

    /// <summary>订阅某个房间（按共享码），幂等。</summary>
    public const string Subscribe = nameof(Subscribe);

    /// <summary>取消订阅，幂等。</summary>
    public const string Unsubscribe = nameof(Unsubscribe);

    /// <summary>把一批归一化后的 DOM 变更推给房间里的其它客户端（仅房主可用）。</summary>
    public const string PublishChanges = nameof(PublishChanges);

    /// <summary>
    /// 取当前存着的整页快照（房间成员可调）。取不到返回 null，调用方应改用请求刷新。
    /// </summary>
    public const string GetSnapshot = nameof(GetSnapshot);

    /// <summary>
    /// 请房主重新交一份整页快照（房间成员可调）。
    ///
    /// 观众端**每次连上来都要调它**：增量只在"订阅之后"才推给这个连接，
    /// 所以只拿一份旧快照 + 订阅后的增量，中间那段变更是补不回来的。
    /// </summary>
    public const string RequestSnapshot = nameof(RequestSnapshot);

    /// <summary>把整页快照交上来（仅房主可用），作为观众端的复现基线。</summary>
    public const string PublishSnapshot = nameof(PublishSnapshot);

    /// <summary>
    /// 查询"我在这个房间里的身份"。
    ///
    /// 这个方法是必需的，不能靠客户端自己猜：<see cref="SharePeerDto.Id"/> 是服务端随机生成的，
    /// 而 <see cref="IShareClient.PeersChanged"/> 给所有人的成员列表是同一份，
    /// 客户端拿不到"哪个是我"。没有它就分不清"房间里只有我自己"和"真有别人在看"。
    /// </summary>
    public const string WhoAmI = nameof(WhoAmI);

    /// <summary>用于客户端测量往返延迟，返回服务端时间戳。</summary>
    public const string Ping = nameof(Ping);
}
