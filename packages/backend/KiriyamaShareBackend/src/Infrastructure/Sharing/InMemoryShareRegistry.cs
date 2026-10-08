using System.Net;
using KiriyamaShareBackend.Application.Common.Exceptions;
using KiriyamaShareBackend.Application.Sharing;
using KiriyamaShareBackend.Shared.Sharing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KiriyamaShareBackend.Infrastructure.Sharing;

/// <summary>
/// 内存版共享房间注册表（进程内单例）。
///
/// 为什么是内存：房间的寿命就等于"房主的连接还活着"——服务重启或房主掉线，房间随即消失，
/// 没有任何需要跨重启存活的状态，为它建表反而要额外处理"孤儿房间"。以后要多实例部署时，
/// 把这里换成带 backplane 的实现即可，Hub 与前端都不用动。
///
/// 线程安全：SignalR 对同一个连接的调用是串行的，但不同连接之间是并发的，
/// 所以对房间表的读写全部收在一把锁里。数据量很小（房间数上限几百），锁的争用可以忽略。
/// </summary>
public class InMemoryShareRegistry : IShareRegistry
{
    /// <summary>房间号字符集：剔掉 0/O/1/I/L 这些手抄时容易认错的。</summary>
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>
    /// 单个节点最多多少个属性。真实页面远到不了这个量级，
    /// 这一项纯粹是防止有人拿一个带十万属性的节点把内存顶爆。
    /// </summary>
    private const int MaxAttributesPerNode = 256;

    private readonly ShareHubSettings _settings;
    private readonly ILogger<InMemoryShareRegistry> _logger;
    private readonly object _gate = new();

    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    /// <summary>connectionId → 它开的房间号。</summary>
    private readonly Dictionary<string, string> _hostedRooms = new(StringComparer.Ordinal);

    /// <summary>connectionId → （房间号 → 自己在该房间里的身份）。只记订阅者，房主不算。</summary>
    private readonly Dictionary<string, Dictionary<string, SharePeerDto>> _subscriptions = new(StringComparer.Ordinal);

    private long _version;
    private int _peerSeq;

    /// <summary>Initializes a new instance of the <see cref="InMemoryShareRegistry"/> class.</summary>
    /// <param name="settings">共享服务配置。</param>
    /// <param name="logger">日志。</param>
    public InMemoryShareRegistry(IOptions<ShareHubSettings> settings, ILogger<InMemoryShareRegistry> logger)
    {
        _settings = settings?.Value ?? new ShareHubSettings();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <inheritdoc/>
    public int RoomCount
    {
        get
        {
            lock (_gate)
            {
                return _rooms.Count;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ShareRoomDto> ListRooms(string connectionId)
    {
        lock (_gate)
        {
            _subscriptions.TryGetValue(connectionId, out var mine);

            return _rooms.Values
                .OrderByDescending(room => room.UpdatedAt)
                .Select(room => ToDto(room, mine is not null && mine.ContainsKey(room.Code)))
                .ToList();
        }
    }

    /// <inheritdoc/>
    public ShareRoomDto StartSharing(string connectionId, ShareStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        lock (_gate)
        {
            // 同一个连接重复发起：只更新共享目标，房间号保持不变
            if (_hostedRooms.TryGetValue(connectionId, out var hostedCode)
                && _rooms.TryGetValue(hostedCode, out var hosted))
            {
                hosted.Title = request.Title.Trim();
                hosted.Url = request.Url.Trim();
                hosted.UpdatedAt = Now();

                // 换了共享目标，旧基线描述的就是另一份页面了 —— 拿它当基线会复现出错误内容。
                // 版本号保留不动：版本是"这个房间里增量的顺序"，跟页面换没换无关；
                // 换页面之后房主会重新交一份基线对齐（订阅者拿到之前先什么都不应用）。
                hosted.Snapshot = null;

                Touch();

                return ToDto(hosted, subscribed: false);
            }

            if (_rooms.Count >= _settings.MaxRooms)
            {
                throw new ConflictException($"Server is at capacity: {_settings.MaxRooms} concurrent sharing rooms.");
            }

            var peer = NewPeer(connectionId, request.Name, existing: null);
            var room = new Room
            {
                Code = NextCode(),
                Title = request.Title.Trim(),
                Url = request.Url.Trim(),
                HostConnectionId = connectionId,
                Host = peer,
                UpdatedAt = Now(),
            };

            room.Members[connectionId] = peer;
            _rooms[room.Code] = room;
            _hostedRooms[connectionId] = room.Code;
            Touch();

            _logger.LogInformation(
                "Sharing started: room={RoomCode} host={ConnectionId} url={Url}",
                room.Code,
                connectionId,
                room.Url);

            return ToDto(room, subscribed: false);
        }
    }

    /// <inheritdoc/>
    public ShareHostedRoom? GetHostedRoom(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_hostedRooms.TryGetValue(connectionId, out var code)
                || !_rooms.TryGetValue(code, out var room))
            {
                return null;
            }

            return new ShareHostedRoom
            {
                Room = ToDto(room, subscribed: false),
                SubscriberConnectionIds = room.Members.Keys
                    .Where(id => id != connectionId)
                    .ToArray(),
            };
        }
    }

    /// <inheritdoc/>
    public ShareAudience? StopSharing(string connectionId)
    {
        lock (_gate)
        {
            if (!_hostedRooms.TryGetValue(connectionId, out var code)
                || !_rooms.TryGetValue(code, out var room))
            {
                return null;
            }

            _logger.LogInformation("Sharing stopped: room={RoomCode} host={ConnectionId}", code, connectionId);

            return CloseRoom(room, actor: connectionId);
        }
    }

    /// <inheritdoc/>
    public ShareAudience Subscribe(string connectionId, string roomCode, string? name)
    {
        var code = NormalizeCode(roomCode);

        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out var room))
            {
                throw new NotFoundException($"Sharing room '{code}' does not exist or has already ended.");
            }

            // 幂等：已经在房间里就什么都不做，也不重复通知
            if (room.Members.ContainsKey(connectionId))
            {
                return new ShareAudience
                {
                    RoomCode = code,
                    ConnectionIds = [],
                    ActorConnectionId = connectionId,
                    Members = room.Members.Values.ToArray(),
                };
            }

            if (!_subscriptions.TryGetValue(connectionId, out var mine))
            {
                mine = new Dictionary<string, SharePeerDto>(StringComparer.Ordinal);
                _subscriptions[connectionId] = mine;
            }

            if (mine.Count >= _settings.MaxSubscriptionsPerConnection)
            {
                throw new ConflictException(
                    $"Too many subscriptions on a single connection: {_settings.MaxSubscriptionsPerConnection}.");
            }

            var peer = NewPeer(connectionId, name, room);
            room.Members[connectionId] = peer;
            mine[code] = peer;
            room.UpdatedAt = Now();
            Touch();

            _logger.LogInformation("Peer joined: room={RoomCode} peer={PeerId}", code, peer.Id);

            return new ShareAudience
            {
                RoomCode = code,
                ConnectionIds = room.Members.Keys.ToArray(),
                ActorConnectionId = connectionId,
                Members = room.Members.Values.ToArray(),
                Peer = peer,
            };
        }
    }

    /// <inheritdoc/>
    public ShareAudience? Unsubscribe(string connectionId, string roomCode)
    {
        var code = NormalizeCode(roomCode);

        lock (_gate)
        {
            // 房间没了就当他已经退出了，保持幂等
            if (!_rooms.TryGetValue(code, out var room)
                || !room.Members.TryGetValue(connectionId, out var peer))
            {
                return null;
            }

            if (room.HostConnectionId == connectionId)
            {
                throw new ConflictException("The host must stop sharing instead of unsubscribing.");
            }

            return Leave(room, connectionId, peer);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ShareAudience> RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            var audiences = new List<ShareAudience>();

            // 1) 它开的房间：关掉，订阅者会收到"房间已关闭"
            if (_hostedRooms.TryGetValue(connectionId, out var hostedCode)
                && _rooms.TryGetValue(hostedCode, out var hosted))
            {
                var closed = CloseRoom(hosted, actor: connectionId);
                audiences.Add(closed with
                {
                    // 已经断开的连接不用再收消息
                    ConnectionIds = closed.ConnectionIds.Where(id => id != connectionId).ToArray(),
                });
            }

            // 2) 它订阅的房间：逐个退出
            if (_subscriptions.Remove(connectionId, out var mine))
            {
                // Leave 会回头改 mine，所以先快照
                foreach (var code in mine.Keys.ToArray())
                {
                    if (_rooms.TryGetValue(code, out var room)
                        && room.Members.ContainsKey(connectionId)
                        && room.Members.TryGetValue(connectionId, out var peer))
                    {
                        audiences.Add(Leave(room, connectionId, peer));
                    }
                }
            }

            return audiences;
        }
    }

    /// <inheritdoc/>
    public SharePeerDto? GetSelf(string connectionId, string roomCode)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(roomCode))
        {
            return null;
        }

        var code = NormalizeCode(roomCode);

        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out var room)
                || !room.Members.ContainsKey(connectionId))
            {
                // 不在这个房间（含房间已关闭）—— 客户端据此清掉"我是谁"
                return null;
            }

            // 房主和订阅者在同一个 Members 表里，直接取就行。
            // SharePeerDto 建好之后就不再改动（进出房间是替换整条记录，不是改字段），所以返回引用是安全的。
            return room.Members[connectionId];
        }
    }

    /// <inheritdoc/>
    public SharePublishResult? PublishChanges(string connectionId, DomPatchDto patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        lock (_gate)
        {
            if (!_hostedRooms.TryGetValue(connectionId, out var code)
                || !_rooms.TryGetValue(code, out var room))
            {
                return null;
            }

            // base 对不上就拒收，绝不"尽力转发"。
            //
            // 这是整个协议里最要紧的一处判断。发送方的状态和房间的权威状态分叉了
            // （上一个 patch 没送到、或者它在连接抖动期间自己往前走了），
            // 这条 patch 里的操作（尤其带下标的 insert / move）在别人的树上会落到错的位置，
            // 而且**不会报错** —— 页面从此静默错位，越叠越偏，谁也看不出是哪一步坏的。
            // 拒收把这个分叉变成一次明确的信号：房主收到之后会重新交一份基线对齐。
            if (patch.BaseVersion != room.Version)
            {
                _logger.LogWarning(
                    "Patch rejected (stale base): room={RoomCode} senderBase={SenderBase} roomVersion={RoomVersion}",
                    code,
                    patch.BaseVersion,
                    room.Version);

                return new SharePublishResult
                {
                    RoomCode = code,
                    ConnectionIds = [],
                    Patch = patch,
                    Rejected = true,
                    Reason = PatchRejectReasons.StaleBase,
                    Version = room.Version,
                };
            }

            // 带不动就**拒收**，绝不"裁一点再转发"。
            //
            // 裁了的话接收方拿到的就不是房主算出来的那组操作，两端的树从这一刻起永久分叉，
            // 而版本号照涨 —— 两边都以为自己是同步的，这是没有任何症状的一类错。
            // 拒收是唯一能保持"版本推进 ⇔ 树按同一组操作前进"这个不变式的做法。
            var forwarded = TryForward(room, patch);

            if (forwarded is null)
            {
                _logger.LogWarning(
                    "Patch rejected (too large): room={RoomCode} senderBase={SenderBase} ops={Ops}",
                    code,
                    patch.BaseVersion,
                    patch.Ops.Count);

                // 注意：**不推进版本**。房间的坐标没有动过，发送方对齐到 Version 之后接着发即可。
                return new SharePublishResult
                {
                    RoomCode = code,
                    ConnectionIds = [],
                    Patch = patch,
                    Rejected = true,
                    Reason = PatchRejectReasons.TooLarge,
                    Version = room.Version,
                };
            }

            // 版本在这里分配：只有服务端知道全局顺序，客户端各算各的会撞车。
            // 注意是 ++ 之后才赋值 —— 第一条 patch 是 1，0 留给"什么都没发生"（基线建立前的状态）。
            room.Version += 1;
            forwarded.Version = room.Version;

            room.UpdatedAt = Now();

            // 注意：这里**不** Touch()。房间内的持续活跃不该把全局版本号刷爆，
            // 否则每个 patch 都会让所有客户端重新拉一次列表。
            return new SharePublishResult
            {
                RoomCode = code,
                ConnectionIds = room.Members.Keys.Where(id => id != connectionId).ToArray(),
                Patch = forwarded,
                Version = room.Version,
            };
        }
    }

    /// <inheritdoc/>
    public DomSnapshotDto? GetSnapshot(string connectionId, string roomCode)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(roomCode))
        {
            return null;
        }

        var code = NormalizeCode(roomCode);

        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out var room) || !room.Members.ContainsKey(connectionId))
            {
                return null;
            }

            // 复制一份再给出去：这份数据会继续被后续的 PublishSnapshot 替换，
            // 直接给引用意味着调用方可能拿到一份正在被改写的对象。
            return room.Snapshot is null ? null : Clone(room.Snapshot);
        }
    }

    /// <inheritdoc/>
    public ShareSnapshotRequest? RequestSnapshot(string connectionId, string roomCode)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(roomCode))
        {
            return null;
        }

        var code = NormalizeCode(roomCode);

        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out var room) || !room.Members.ContainsKey(connectionId))
            {
                return null;
            }

            return new ShareSnapshotRequest
            {
                RoomCode = room.Code,
                HostConnectionId = room.HostConnectionId,
                SubscriberConnectionIds = room.Members.Keys
                    .Where(id => id != room.HostConnectionId)
                    .ToArray(),
            };
        }
    }

    /// <inheritdoc/>
    public ShareSnapshotResult? PublishSnapshot(string connectionId, DomSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (!_hostedRooms.TryGetValue(connectionId, out var code)
                || !_rooms.TryGetValue(code, out var room))
            {
                // 不是房主（或者已经不在共享了）。快照必须来自被共享的那份 DOM，
                // 允许别人塞进来等于让任何人替房间决定画什么。
                return null;
            }

            // 根节点必须能盖住整个页面（文档节点或元素）。文本/注释当"根"会让观众端重建出一片空白，
            // 属于调用方 bug，要炸出来而不是静默存下。
            if (snapshot.Root is null || !snapshot.Root.CanBeRoot)
            {
                throw new CustomException("Snapshot root element is required.", null, HttpStatusCode.BadRequest);
            }

            var pruned = Prune(snapshot.Root, _settings.MaxSnapshotNodes, _settings.MaxSnapshotChars);

            var stored = new DomSnapshotDto
            {
                // 房间号、地址、版本一律以服务端为准，不采信发送方
                RoomCode = room.Code,
                Url = room.Url,
                Title = string.IsNullOrWhiteSpace(snapshot.Title) ? room.Title : snapshot.Title.Trim(),
                Doctype = string.IsNullOrWhiteSpace(snapshot.Doctype) ? "html" : snapshot.Doctype.Trim(),
                Root = pruned.Root,

                // 发送方自己标了截断，或者我们这一层又裁了一次 —— 两种情况都要如实标出来
                Truncated = snapshot.Truncated || pruned.Truncated,
                Version = room.Version,
                At = Now(),
            };

            room.Snapshot = stored;

            return new ShareSnapshotResult
            {
                RoomCode = room.Code,
                SubscriberConnectionIds = room.Members.Keys
                    .Where(id => id != room.HostConnectionId)
                    .ToArray(),
                Snapshot = Clone(stored),
            };
        }
    }

    /// <summary>
    /// 关房间：摘掉所有相关记录，返回"该通知原来那批人"的受众。
    /// 调用方必须已经持有 <see cref="_gate"/>。
    /// </summary>
    private ShareAudience CloseRoom(Room room, string? actor)
    {
        foreach (var memberConnectionId in room.Members.Keys)
        {
            if (_subscriptions.TryGetValue(memberConnectionId, out var mine))
            {
                mine.Remove(room.Code);
            }

            if (_hostedRooms.TryGetValue(memberConnectionId, out var owned) && owned == room.Code)
            {
                _hostedRooms.Remove(memberConnectionId);
            }
        }

        var audience = new ShareAudience
        {
            RoomCode = room.Code,
            ConnectionIds = room.Members.Keys.ToArray(),
            ActorConnectionId = actor,
            Members = room.Members.Values.ToArray(),
            RoomClosed = true,
        };

        room.Members.Clear();
        _rooms.Remove(room.Code);
        Touch();

        return audience;
    }

    /// <summary>
    /// 退出房间。调用方必须已经持有 <see cref="_gate"/>。
    /// </summary>
    private ShareAudience Leave(Room room, string connectionId, SharePeerDto peer)
    {
        room.Members.Remove(connectionId);

        if (_subscriptions.TryGetValue(connectionId, out var mine))
        {
            mine.Remove(room.Code);
        }

        room.UpdatedAt = Now();
        Touch();

        return new ShareAudience
        {
            RoomCode = room.Code,
            ConnectionIds = room.Members.Keys.ToArray(),
            ActorConnectionId = connectionId,
            Members = room.Members.Values.ToArray(),
            Peer = peer,
        };
    }

    /// <summary>
    /// 试着把一条 patch **原样**带过去。
    ///
    /// 这是一个能力检查，不是一次整形：**任何值都不改动**。
    ///
    /// 这一条是整个协议里最容易被写错、而且错了**完全没有症状**的地方。上一版这里做的是
    /// "补时间戳、限条数、截断超长值、丢掉看不懂的操作" —— 听起来很稳妥，实际上是：
    /// 房主算出来的 600 条操作，服务端悄悄只转发了 500 条，另外 100 条的变化**永远消失**；
    /// 一个属性值被截断成 4096 字符，接收方拿到的字符串和发送方手里那个不一样。
    /// 两边都以为自己是同步的（版本号照涨），但树已经不是同一棵了，而且没有任何信号。
    ///
    /// 所以现在要么完整带过去，要么返回 null 让调用方拒收。没有中间状态。
    /// 房间号与版本仍然要重新盖 —— 那是"谁在什么坐标上"的权威信息，
    /// 不影响操作本身的语义。
    /// </summary>
    private DomPatchDto? TryForward(Room room, DomPatchDto patch)
    {
        if (patch.Ops.Count > _settings.MaxOpsPerPatch)
        {
            return null;
        }

        var ops = new List<DomOpDto>(patch.Ops.Count);

        foreach (var op in patch.Ops)
        {
            var forwarded = TryForwardOp(op);
            if (forwarded is null)
            {
                return null;
            }

            ops.Add(forwarded);
        }

        return new DomPatchDto
        {
            Id = string.IsNullOrEmpty(patch.Id) ? Guid.NewGuid().ToString("N") : patch.Id,
            Url = string.IsNullOrEmpty(patch.Url) ? room.Url : patch.Url,

            // 房间号以服务端为准：发送方写的不可信，也防止把变更导到别的房间
            RoomCode = room.Code,

            // base 是发送方的声明（上面已经比对过），version 紧接着会被服务端重新赋值
            BaseVersion = patch.BaseVersion,
            Version = room.Version,
            Ops = ops,
            At = patch.At > 0 ? patch.At : Now(),
        };
    }

    /// <summary>
    /// 一条操作能不能原样带过去。带不动（超限）返回 null，调用方据此拒收整条 patch。
    ///
    /// 认不出的种类**直接抛**，不是丢掉：那说明两端的协议版本对不上。丢掉等于静默改坏页面
    /// 的一部分，而重新交基线也救不回来（发送方还会继续发它，变成来回打转），
    /// 所以必须立刻、响亮地暴露出来。
    /// </summary>
    private DomOpDto? TryForwardOp(DomOpDto? op)
    {
        if (op is null)
        {
            return null;
        }

        switch (op.Op)
        {
            case DomOpKinds.Insert:
            {
                // 一棵空树没有可插的东西，属于发送方的 bug
                if (op.Nodes is null || op.Nodes.Count == 0)
                {
                    return null;
                }

                var (nodes, chars) = Measure(op.Nodes, _settings.MaxInsertNodes, _settings.MaxSnapshotChars);
                if (nodes > _settings.MaxInsertNodes || chars > _settings.MaxSnapshotChars)
                {
                    return null;
                }

                return op;
            }

            case DomOpKinds.Remove:
                // Parent 是可选的交叉校验，不是必须的：接收方按 Id 找到节点就能删
                return op;

            case DomOpKinds.Attrs:
            {
                if (op.Set is null && op.Remove is null)
                {
                    return null;
                }

                if (op.Set is { Count: > MaxAttributesPerNode }
                    || op.Remove is { Count: > MaxAttributesPerNode })
                {
                    return null;
                }

                if (op.Set is not null
                    && op.Set.Any(pair => pair.Value.Length > _settings.MaxValueLength))
                {
                    return null;
                }

                return op;
            }

            case DomOpKinds.Text:
                return (op.Text?.Length ?? 0) > _settings.MaxValueLength ? null : op;

            default:
                throw new CustomException(
                    $"Unknown DOM op kind '{op.Op}'.",
                    null,
                    HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// 量一组树的规模，用于"这条操作带得动吗"的判断。
    ///
    /// 超过上限就**提前停**：一个塞进来的巨型子树不该让服务端把整棵树走完才算得出来
    /// 它太大了。
    /// </summary>
    private static (int Nodes, int Chars) Measure(
        IEnumerable<SerializedNodeDto> nodes,
        int maxNodes,
        int maxChars)
    {
        var budget = new PruneBudget();
        var edges = new Stack<SerializedNodeDto>(nodes);

        while (edges.Count > 0)
        {
            if (budget.Nodes > maxNodes || budget.Chars > maxChars)
            {
                break;
            }

            var node = edges.Pop();
            budget.Nodes += 1;
            budget.Chars += node.Text?.Length ?? 0;

            if (node.Attrs is not null)
            {
                foreach (var pair in node.Attrs)
                {
                    budget.Chars += pair.Key.Length + pair.Value.Length;
                }
            }

            if (node.Children is not null)
            {
                foreach (var child in node.Children)
                {
                    edges.Push(child);
                }
            }
        }

        return (budget.Nodes, budget.Chars);
    }

    /// <summary>
    /// 按预算裁剪一棵树。返回裁剪后的树，以及"到底有没有裁掉东西"。
    ///
    /// 两个预算一起看：节点数兜住"结构很大"，字符数兜住"结构不大但文本巨多"
    /// —— 只有前者会被两万个各挂 4K 文本的节点顶爆。
    /// </summary>
    private (SerializedNodeDto Root, bool Truncated) Prune(SerializedNodeDto root, int maxNodes, int maxChars)
    {
        var budget = new PruneBudget();
        var kept = CopyWithin(root, budget, maxNodes, maxChars);

        // 根节点永远不会被丢掉（预算是几万个节点，不可能在第 0 个就用完），
        // 这里只是把"万一"兜住，免得返回 null 让上层去处理一个不该发生的分支。
        return (kept ?? new SerializedNodeDto { Id = root.Id, Type = root.Type, Tag = root.Tag }, budget.Truncated);
    }

    /// <summary>
    /// 复制一个节点（含子树），累加预算；预算用完返回 null。
    /// 预算用尽时**停在第一个放不下的节点**上，而不是继续往下试 —— 后面多半同样放不下。
    /// </summary>
    private SerializedNodeDto? CopyWithin(SerializedNodeDto node, PruneBudget budget, int maxNodes, int maxChars)
    {
        if (budget.Nodes >= maxNodes || budget.Chars >= maxChars)
        {
            budget.Truncated = true;
            return null;
        }

        budget.Nodes += 1;
        budget.Chars += node.Text?.Length ?? 0;

        var copy = new SerializedNodeDto();

        // 标量字段集中复制（含 Type / RootId / IsSvg / CompatMode 这些）。
        // 刻意不在两处各写一遍字段列表 —— 漏一个只是线上少个字段、观众端悄悄少一点东西，不会报错。
        copy.CopyScalarsFrom(node);
        copy.Text = Truncate(node.Text, _settings.MaxValueLength);

        if (copy.Attrs is { Count: > 0 })
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in copy.Attrs.Take(MaxAttributesPerNode))
            {
                var value = Truncate(pair.Value, _settings.MaxValueLength) ?? string.Empty;
                attrs[pair.Key] = value;
                budget.Chars += pair.Key.Length + value.Length;
            }

            copy.Attrs = attrs;
        }

        if (node.Children is { Count: > 0 })
        {
            foreach (var child in node.Children)
            {
                var keptChild = CopyWithin(child, budget, maxNodes, maxChars);
                if (keptChild is null)
                {
                    break;
                }

                (copy.Children ??= new List<SerializedNodeDto>()).Add(keptChild);
            }
        }
        else if (node.Children is not null)
        {
            // **空数组要原样留着。**
            //
            // rrweb 的 `buildNodeWithSN()` 对文档与元素是无条件 `for...of n.childNodes` 的，
            // 字段缺失直接 `TypeError`，整份基线一个字都建不出来。采集端为了守住这条不变量
            // 会恒给 `childNodes`（没有子节点就给 `[]`），这里要是把它归一成 null，
            // 就等于服务端亲手把这条不变量拆了 —— 而且症状在客户端，日志里什么都看不到。
            copy.Children = new List<SerializedNodeDto>();
        }

        return copy;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength
            ? value[..maxLength]
            : value;

    /// <summary>深拷贝一份基线。基线会继续被后续的 PublishSnapshot 替换，直接给引用会让调用方看到半新半旧的对象。</summary>
    private static DomSnapshotDto Clone(DomSnapshotDto snapshot) => new()
    {
        RoomCode = snapshot.RoomCode,
        Url = snapshot.Url,
        Title = snapshot.Title,
        Doctype = snapshot.Doctype,
        Root = CloneNode(snapshot.Root),
        Truncated = snapshot.Truncated,
        Version = snapshot.Version,
        At = snapshot.At,
    };

    private static SerializedNodeDto CloneNode(SerializedNodeDto node)
    {
        var copy = new SerializedNodeDto();
        copy.CopyScalarsFrom(node);
        copy.Children = node.Children?.Select(CloneNode).ToList();
        return copy;
    }

    /// <summary>分配一个空闲房间号。调用方必须已经持有 <see cref="_gate"/>。</summary>
    private string NextCode()
    {
        var length = Math.Clamp(_settings.RoomCodeLength, 4, 16);

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var chars = new char[length];
            for (var index = 0; index < length; index++)
            {
                chars[index] = CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)];
            }

            var code = new string(chars);
            if (!_rooms.ContainsKey(code))
            {
                return code;
            }
        }

        throw new ConflictException("Could not allocate a free sharing code, please retry.");
    }

    /// <summary>
    /// 造一个房间成员。颜色优先挑房间里还没被占用的，占满了才允许重复。
    /// 调用方必须已经持有 <see cref="_gate"/>。
    /// </summary>
    private SharePeerDto NewPeer(string connectionId, string? name, Room? existing)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var palette = _settings.PeerColors is { Length: > 0 }
            ? _settings.PeerColors
            : ["#e0533d"];

        var used = existing?.Members.Values.Select(member => member.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var color = palette.FirstOrDefault(candidate => used is null || !used.Contains(candidate))
                    ?? palette[_peerSeq % palette.Length];

        _peerSeq++;

        return new SharePeerDto
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? $"Guest-{id[..4]}" : name!.Trim(),
            Color = color,
        };
    }

    private static ShareRoomDto ToDto(Room room, bool subscribed) => new()
    {
        Id = room.Code,
        Title = room.Title,
        Url = room.Url,
        Host = room.Host.Name,
        PeerCount = room.Members.Count,
        LatencyMs = null,
        UpdatedAt = room.UpdatedAt,
        Subscribed = subscribed,
    };

    /// <summary>房间列表版本 +1。调用方必须已经持有 <see cref="_gate"/>。</summary>
    private void Touch() => _version++;

    private static string NormalizeCode(string roomCode) =>
        string.IsNullOrWhiteSpace(roomCode) ? string.Empty : roomCode.Trim().ToUpperInvariant();

    private static void Validate(ShareStartRequest request)
    {
        // 入参校验不在这里：本类是单例，注入不了 scoped 的 validator，
        // 所以校验放在传输层边界（ShareHub.StartSharing，走 FluentValidation）。
        // 这里只兜住"调用方完全不校验"的极端情况，保证房间不会带着空标题建起来。
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new CustomException("Page title is required.", null, HttpStatusCode.BadRequest);
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CustomException("Page url must be an absolute http/https url.", null, HttpStatusCode.BadRequest);
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>裁剪树时的累计用量。可变对象，递归里一路传下去。</summary>
    private sealed class PruneBudget
    {
        /// <summary>已经收下的节点数。</summary>
        public int Nodes { get; set; }

        /// <summary>已经收下的文本 + 属性字符数。</summary>
        public int Chars { get; set; }

        /// <summary>是否因为预算不足裁掉了东西。</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>一个共享房间。只在房主在线期间存在。</summary>
    private sealed class Room
    {
        public string Code { get; init; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string HostConnectionId { get; set; } = string.Empty;

        public SharePeerDto Host { get; set; } = new();

        public long UpdatedAt { get; set; }

        /// <summary>
        /// 房间的当前版本：**已经接受过的 patch 条数**。
        ///
        /// 它是"共同基线"的坐标 —— 基线带着它、每个 patch 声明自己基于哪个版本，
        /// 服务端只在两者对得上时才接受。观众凭它判断自己手里的树是不是还接得上下一批增量。
        /// </summary>
        public long Version { get; set; }

        /// <summary>当前整页基线（房主交上来的）。观众端的重建起点，还没交过时为 null。</summary>
        public DomSnapshotDto? Snapshot { get; set; }

        /// <summary>全部成员（含房主），connectionId → 身份。</summary>
        public Dictionary<string, SharePeerDto> Members { get; } = new(StringComparer.Ordinal);
    }
}
