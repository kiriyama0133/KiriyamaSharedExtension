using KiriyamaShareBackend.Shared.Sharing;

namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 共享房间的注册表。
///
/// 房间是"活"的运行时状态：房主在线才存在，房主断了房间就没了，所以没有任何需要落库的东西。
/// 这一层只管状态与规则，不碰 SignalR —— 它负责算出"这条消息该发给哪些连接"，
/// 具体怎么发由 Hub 决定。这样换传输层（比如以后加 backplane 或换成裸 WebSocket）时这层不用动。
/// </summary>
public interface IShareRegistry
{
    /// <summary>房间列表的版本号。房间增删、成员变化时 +1，客户端据此判断要不要重新拉列表。</summary>
    long Version { get; }

    /// <summary>当前房间总数。</summary>
    int RoomCount { get; }

    /// <summary>
    /// 列出所有房间。<paramref name="connectionId"/> 决定每个房间的
    /// <see cref="ShareRoomDto.Subscribed"/> 怎么算——它是按调用者各算各的。
    /// </summary>
    /// <param name="connectionId">发起查询的连接。</param>
    /// <returns>按最近更新排序的房间快照。</returns>
    IReadOnlyList<ShareRoomDto> ListRooms(string connectionId);

    /// <summary>
    /// 发起共享。同一个连接重复调用（例如 SPA 里换了页面）是幂等的：
    /// 只更新共享目标，房间号不变。
    /// </summary>
    /// <param name="connectionId">连接 id。</param>
    /// <param name="request">被共享页面的信息。</param>
    /// <returns>新建或更新后的房间快照。</returns>
    ShareRoomDto StartSharing(string connectionId, ShareStartRequest request);

    /// <summary>
    /// 房主当前正在共享的房间（含订阅者名单）。
    ///
    /// 存在的理由：<see cref="StartSharing"/> 是幂等的（同一个连接重复发起只更新共享目标、
    /// 房间号不变），所以调用方没法从它的返回值看出"这次到底是不是换了页面"。
    /// 先取一次旧值再比一次，才能把"跳转"识别出来 —— 跳转要作为事件推给订阅者，
    /// 并且要重新交一份整页快照。
    /// </summary>
    /// <param name="connectionId">连接 id。</param>
    /// <returns>该连接正在共享的房间；它没在共享时返回 null。</returns>
    ShareHostedRoom? GetHostedRoom(string connectionId);

    /// <summary>结束共享并关闭房间。</summary>
    /// <param name="connectionId">连接 id。</param>
    /// <returns>需要通知的对象；该连接没有在共享时返回 null。</returns>
    ShareAudience? StopSharing(string connectionId);

    /// <summary>按共享码订阅一个房间。已经在房间里时幂等，且不会重复通知。</summary>
    /// <param name="connectionId">连接 id。</param>
    /// <param name="roomCode">房间号 / 共享码。</param>
    /// <param name="name">订阅者希望显示的名字，可空。</param>
    /// <returns>需要通知的对象。</returns>
    ShareAudience Subscribe(string connectionId, string roomCode, string? name);

    /// <summary>退出房间。房主不能用这个，要先 StopSharing。</summary>
    /// <param name="connectionId">连接 id。</param>
    /// <param name="roomCode">房间号。</param>
    /// <returns>需要通知的对象；本来就不在房间里时返回 null。</returns>
    ShareAudience? Unsubscribe(string connectionId, string roomCode);

    /// <summary>
    /// 连接断开时调用：关掉它开的房间、退掉它订阅的房间。
    /// </summary>
    /// <param name="connectionId">断开的连接。</param>
    /// <returns>每一项都是一次需要广播的变化（可能为空）。</returns>
    IReadOnlyList<ShareAudience> RemoveConnection(string connectionId);

    /// <summary>
    /// 查询某个连接在某个房间里的身份（房主或订阅者都算）。
    ///
    /// 存在的理由：<see cref="IShareClient.PeersChanged"/> 给所有人的成员列表是同一份，
    /// 而成员 id 是随机生成的，客户端无法判断"哪个是我"。
    /// </summary>
    /// <param name="connectionId">连接 id。</param>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <returns>该连接在这个房间里的身份；不在这个房间里时返回 null。</returns>
    SharePeerDto? GetSelf(string connectionId, string roomCode);

    /// <summary>
    /// 把一个 patch 交给房间里的其它人。只有该房间的房主能推。
    ///
    /// 会先校验 <see cref="DomPatchDto.BaseVersion"/> 是否等于房间的当前版本：对不上就拒收
    /// （见 <see cref="SharePublishResult.Rejected"/>），不做任何"尽力而为"的转发 ——
    /// 把一条基于错误基线的增量发出去，接收方会静默地错位，比丢一条更糟。
    /// </summary>
    /// <param name="connectionId">推送上来的连接。</param>
    /// <param name="patch">基于某个已知版本的增量。</param>
    /// <returns>接收方与处理结果；不在共享、或不是房主时返回 null。</returns>
    SharePublishResult? PublishChanges(string connectionId, DomPatchDto patch);

    /// <summary>
    /// 取当前存着的整页基线。只有房间成员取得到 —— 它等于把被共享的页面整个交出去，
    /// 不该让知道房间号以外的人随便拉。
    /// </summary>
    /// <param name="connectionId">调用方连接。</param>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <returns>基线；房间不存在、调用方不在房间、或还没人交过时返回 null。</returns>
    DomSnapshotDto? GetSnapshot(string connectionId, string roomCode);

    /// <summary>
    /// 解析"该请谁交快照"。房主已经掉线之类的情况下返回 null。
    /// </summary>
    /// <param name="connectionId">调用方连接（必须是房间成员）。</param>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <returns>房主连接与订阅者列表；调用方不在房间里时返回 null。</returns>
    ShareSnapshotRequest? RequestSnapshot(string connectionId, string roomCode);

    /// <summary>
    /// 存入一份整页基线，并把房间的版本对齐到它。
    /// 只有房间房主能交。
    ///
    /// 它同时是"分叉之后的复位动作"：房主推 patch 被拒（base 对不上）时，
    /// 交一份新的基线就能让两端重新站在同一个版本上。
    /// </summary>
    /// <param name="connectionId">提交方连接。</param>
    /// <param name="snapshot">基线内容（房间号、版本、时间由服务端盖章）。</param>
    /// <returns>入库结果与需要通知的订阅者；提交方不是房主时返回 null。</returns>
    ShareSnapshotResult? PublishSnapshot(string connectionId, DomSnapshotDto snapshot);
}
