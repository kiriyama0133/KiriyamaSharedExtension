using FluentValidation;
using KiriyamaShareBackend.Application.Common.Exceptions;
using KiriyamaShareBackend.Application.Sharing;
using KiriyamaShareBackend.Shared.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace KiriyamaShareBackend.Infrastructure.Sharing;

/// <summary>
/// 共享服务的实时通道（SignalR，默认走 WebSocket）。
///
/// 这是本服务唯一的 Hub。模板原本自带的 <c>NotificationHub</c>（按租户分组的登录用户通知通道）
/// 已随通知模块一并移除 —— 共享房间的参与者是浏览器扩展，没有账号体系，靠房间号（共享码）进入，
/// 两者的受众与鉴权模型完全不同，混在一个 Hub 里最后会被迫给每个方法单独放宽鉴权。
///
/// 访问控制目前只有房间号这一层（6 位短码）。这是有意为之的取舍：
/// 分享链接本身就是要给别人的。要更严的话下一步是给房间加口令或一次性 token。
///
/// 传入参数用对象（而不是一长串位置参数），是为了以后加字段不破坏老客户端。
/// </summary>
[AllowAnonymous]
public class ShareHub : Hub<IShareClient>
{
    private readonly IShareRegistry _registry;
    private readonly IValidator<ShareStartRequest> _validator;
    private readonly ILogger<ShareHub> _logger;

    /// <summary>Initializes a new instance of the <see cref="ShareHub"/> class.</summary>
    /// <param name="registry">共享房间注册表。</param>
    /// <param name="validator">发起共享的入参校验器。</param>
    /// <param name="logger">日志。</param>
    public ShareHub(IShareRegistry registry, IValidator<ShareStartRequest> validator, ILogger<ShareHub> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        _logger.LogInformation("Client connected to ShareHub: {ConnectionId}", Context.ConnectionId);

        // 连上就告诉它当前版本，客户端据此决定要不要拉列表
        await Clients.Caller.RoomsChanged(_registry.Version, _registry.RoomCount);
    }

    /// <inheritdoc/>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // 连接**为什么**掉，全靠这一条。以前 exception 参数被直接扔掉，日志里只剩
        // 一句 "Client disconnected"，读起来像"正常断开"；可房间的寿命等于房主的连接，
        // 于是"房间刚建好就没了"这类问题在日志里**完全看不出原因** —— 只能看到
        // "Sharing started" 紧跟着 "Client disconnected"。
        //
        // 尤其要注意：消息超过 MaximumReceiveMessageSize 时，SignalR 会直接把连接
        // abort 掉，房主那边只拿到一条 HubException。那正是这条日志要抓的东西。
        if (exception is not null)
        {
            _logger.LogWarning(
                exception,
                "ShareHub connection dropped with an error: {ConnectionId}",
                Context.ConnectionId);
        }

        try
        {
            var audiences = _registry.RemoveConnection(Context.ConnectionId);
            foreach (var audience in audiences)
            {
                await DispatchAsync(audience, ShareEventKinds.Leave);
            }

            if (audiences.Count > 0)
            {
                await NotifyRoomsChangedAsync();
            }

            _logger.LogInformation("Client disconnected from ShareHub: {ConnectionId}", Context.ConnectionId);
        }
        catch (Exception ex)
        {
            // 断开阶段再抛异常没有意义，只会掩盖真正的原因
            _logger.LogError(ex, "Failed to clean up after {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// 拉取房间列表。每个房间的 <see cref="ShareRoomDto.Subscribed"/> 是按调用者算的。
    /// </summary>
    /// <returns>当前房间快照。</returns>
    public Task<ShareRoomDto[]> ListRooms() =>
        Task.FromResult(_registry.ListRooms(Context.ConnectionId).ToArray());

    /// <summary>
    /// 发起共享。幂等：重复调用只更新共享目标，房间号不变。
    ///
    /// 共享的页面**换了地址**时（SPA 路由跳转、或整页跳转后重新接上）房间不关，
    /// 而是朝订阅者推一条 <see cref="ShareEventKinds.Navigate"/>：他们要据此去取一份新的
    /// 整页快照当基线，旧基线描述的是另一个页面，继续叠增量只会越叠越错。
    /// </summary>
    /// <param name="request">被共享页面的信息。</param>
    /// <returns>房间快照，其中的 <c>id</c> 就是共享码。</returns>
    public async Task<ShareRoomDto> StartSharing(ShareStartRequest request)
    {
        // 校验放在边界：注册表是单例，注入不了 scoped 的 validator（见 ShareStartRequestValidator 的注释）
        if (request is null)
        {
            throw new HubException("Share start request is required.");
        }

        var validation = await _validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            throw new HubException(string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        // 先取一次旧值：StartSharing 幂等（房间号不变），只看它的返回值分不出
        // "新建" 和 "换了页面"，而这两者该做的事完全不同。
        var previous = _registry.GetHostedRoom(Context.ConnectionId);

        var room = Translate(() => _registry.StartSharing(Context.ConnectionId, request));
        await NotifyRoomsChangedAsync();

        if (previous is not null && !string.Equals(previous.Room.Url, room.Url, StringComparison.Ordinal))
        {
            await NotifyNavigatedAsync(previous, room);
        }

        return room;
    }

    /// <summary>共享的页面跳到了新地址：告诉房间里的订阅者。房主自己不用收。</summary>
    private async Task NotifyNavigatedAsync(ShareHostedRoom previous, ShareRoomDto room)
    {
        if (previous.SubscriberConnectionIds.Length == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Sharing target navigated: room={RoomCode} from={From} to={To}",
            room.Id,
            previous.Room.Url,
            room.Url);

        await Clients.Clients(previous.SubscriberConnectionIds).ShareEventReceived(new ShareEventDto
        {
            Kind = ShareEventKinds.Navigate,
            RoomCode = room.Id,
            Peer = null,
            Url = room.Url,
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>
    /// 结束共享，房间随即关闭，订阅者会收到一条 <see cref="ShareEventKinds.System"/> 事件。
    /// </summary>
    /// <returns>本次是否真的关掉了一个房间。</returns>
    public async Task<bool> StopSharing()
    {
        var audience = Translate(() => _registry.StopSharing(Context.ConnectionId));
        if (audience is null)
        {
            return false;
        }

        await DispatchAsync(audience, ShareEventKinds.Leave);
        await NotifyRoomsChangedAsync();
        return true;
    }

    /// <summary>
    /// 订阅某个房间。
    /// </summary>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <param name="name">订阅者显示名，可空（服务端会分配一个）。</param>
    /// <returns>进入的房间号。</returns>
    public async Task<string> Subscribe(string roomCode, string? name = null)
    {
        var audience = Translate(() => _registry.Subscribe(Context.ConnectionId, roomCode, name));

        await DispatchAsync(audience, ShareEventKinds.Join);
        await NotifyRoomsChangedAsync();

        return audience.RoomCode;
    }

    /// <summary>
    /// 退出房间。房主要退出得用 <see cref="StopSharing"/>。
    /// </summary>
    /// <param name="roomCode">房间号。</param>
    /// <returns>本次是否真的退出了一个房间。</returns>
    public async Task<bool> Unsubscribe(string roomCode)
    {
        var audience = Translate(() => _registry.Unsubscribe(Context.ConnectionId, roomCode));
        if (audience is null)
        {
            return false;
        }

        await DispatchAsync(audience, ShareEventKinds.Leave);
        await NotifyRoomsChangedAsync();
        return true;
    }

    /// <summary>
    /// 把一个 patch 推给房间里的其它客户端。只有房间的发起方可以调用，
    /// 调用者自己不会收到回环。
    /// </summary>
    /// <param name="patch">基于某个已知版本的增量。</param>
    /// <returns>
    /// 回执。**不是 bool**：房主要靠它区分"发出去了""base 过时了""我已经不是房主了"，
    /// 这三种情况的后续动作完全不同（前者什么都不用做，中者重建基线，后者清掉本地房间状态）。
    /// </returns>
    public async Task<DomPatchAckDto> PublishChanges(DomPatchDto patch)
    {
        var result = Translate(() => _registry.PublishChanges(Context.ConnectionId, patch));
        if (result is null)
        {
            return new DomPatchAckDto
            {
                Accepted = false,
                Version = 0,
                Reason = PatchRejectReasons.NotHosting,
            };
        }

        if (result.Rejected)
        {
            // 一条被拒的 patch 不转发给任何人 —— 见 InMemoryShareRegistry.PublishChanges 的注释。
            // 原因原样带过去：房主要靠它区分"重建基线"（base 过期 / 太大）
            // 和"清掉本地房间状态"（已经不是房主）。
            return new DomPatchAckDto
            {
                Accepted = false,
                Version = result.Version,
                Reason = string.IsNullOrEmpty(result.Reason) ? PatchRejectReasons.StaleBase : result.Reason,
            };
        }

        if (result.ConnectionIds.Length > 0)
        {
            await Clients.Clients(result.ConnectionIds).ChangesReceived(result.Patch);
        }

        return new DomPatchAckDto
        {
            Accepted = true,
            Version = result.Version,
        };
    }

    /// <summary>
    /// 取当前存着的整页基线。取不到（还没人交过、或调用方不在房间里）返回 null。
    /// </summary>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <returns>整页基线（带 id 的节点树）。</returns>
    public Task<DomSnapshotDto?> GetSnapshot(string roomCode) =>
        Task.FromResult(_registry.GetSnapshot(Context.ConnectionId, roomCode));

    /// <summary>
    /// 请房主重新交一份整页基线。
    ///
    /// 观众端连上来时**一定要调它**，不能只取已有的那份：增量只推给"已经订阅"的连接，
    /// 所以旧基线 + 订阅之后的增量，中间那段永远补不回来（页面会缺一块而且看不出来）。
    /// 观众端发现 base 对不上（丢过 patch）时也是靠它复位。
    ///
    /// 是异步的：服务端只能把请求转给房主，房主交上来之后再推 <see cref="IShareClient.SnapshotReady"/>，
    /// 调用方收到那条消息后再调 <see cref="GetSnapshot"/>。
    /// </summary>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <returns>是否已经把请求转给了房主。</returns>
    public async Task<bool> RequestSnapshot(string roomCode)
    {
        var request = Translate(() => _registry.RequestSnapshot(Context.ConnectionId, roomCode));
        if (request is null || string.IsNullOrEmpty(request.HostConnectionId))
        {
            return false;
        }

        await Clients.Client(request.HostConnectionId).SnapshotRequested(request.RoomCode);
        return true;
    }

    /// <summary>
    /// 把整页基线交上来（仅房主）。落库后通知房间里的订阅者去取。
    /// 它同时是"分叉之后的复位动作"：房主推 patch 被拒时交一份新的即可对齐。
    /// </summary>
    /// <param name="snapshot">整页基线。</param>
    /// <returns>本份基线是否被接收（不是房主时为 false）。</returns>
    public async Task<bool> PublishSnapshot(DomSnapshotDto snapshot)
    {
        var result = Translate(() => _registry.PublishSnapshot(Context.ConnectionId, snapshot));
        if (result is null)
        {
            return false;
        }

        if (result.SubscriberConnectionIds.Length > 0)
        {
            await Clients.Clients(result.SubscriberConnectionIds)
                .SnapshotReady(result.RoomCode, result.Snapshot.Version);
        }

        return true;
    }

    /// <summary>
    /// 查询调用者自己在某个房间里的身份（拿回自己分配到的 id、名字、颜色）。
    ///
    /// 客户端需要它来判断 <see cref="IShareClient.PeersChanged"/> 给的成员列表里哪个是自己 ——
    /// 成员 id 是服务端随机生成的，客户端没有别的办法知道。
    /// 不在这个房间里（含房间已关闭）返回 null，客户端据此清掉本地缓存的"我是谁"。
    /// </summary>
    /// <param name="roomCode">房间号 / 共享码，大小写不敏感。</param>
    /// <returns>调用者自己的成员信息。</returns>
    public Task<SharePeerDto?> WhoAmI(string roomCode) =>
        Task.FromResult(_registry.GetSelf(Context.ConnectionId, roomCode));

    /// <summary>往返延迟测量用：原样返回服务端时间戳（epoch 毫秒）。</summary>
    /// <returns>服务端当前时间。</returns>
    public Task<long> Ping() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>把一次成员变化推给该收到它的人。</summary>
    private async Task DispatchAsync(ShareAudience audience, string kind)
    {
        if (audience.ConnectionIds.Length > 0)
        {
            await Clients.Clients(audience.ConnectionIds).PeersChanged(audience.RoomCode, audience.Members);
        }

        // 触发者自己不用收"我进/我出"这条
        var targets = audience.ConnectionIds
            .Where(id => id != audience.ActorConnectionId)
            .ToArray();

        if (targets.Length == 0)
        {
            return;
        }

        await Clients.Clients(targets).ShareEventReceived(new ShareEventDto
        {
            Kind = audience.RoomClosed ? ShareEventKinds.System : kind,
            RoomCode = audience.RoomCode,
            Peer = audience.Peer,
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private Task NotifyRoomsChangedAsync() =>
        Clients.All.RoomsChanged(_registry.Version, _registry.RoomCount);

    /// <summary>
    /// 把领域异常翻成 <see cref="HubException"/>。
    ///
    /// 这一步不能省：SignalR 只会把 HubException 的 message 原样发给客户端，
    /// 其它异常一律被压成 "An unexpected error occurred invoking ..."，
    /// 前端就永远看不到"房间号不对"这种真正有用的信息了。
    /// </summary>
    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (CustomException ex)
        {
            // 内部异常细节只在服务端日志里出现，别外泄
            throw new HubException(ex.Message);
        }
    }
}
