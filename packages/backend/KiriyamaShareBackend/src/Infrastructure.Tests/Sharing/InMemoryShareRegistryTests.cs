using FluentAssertions;
using KiriyamaShareBackend.Application.Common.Exceptions;
using KiriyamaShareBackend.Application.Sharing;
using KiriyamaShareBackend.Infrastructure.Sharing;
using KiriyamaShareBackend.Shared.Sharing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace KiriyamaShareBackend.Infrastructure.Tests.Sharing;

/// <summary>
/// <see cref="InMemoryShareRegistry"/> 的规则测试。
///
/// 这一层是整个共享服务里唯一有真实业务规则的地方（Hub 只负责转发），所以边界全部落在这里：
/// 房间生命周期、幂等性、房主与订阅者的权限差异、容量与防爆限制、断线清理。
/// </summary>
public class InMemoryShareRegistryTests
{
    /// <summary>房间号字符集：剔掉 0/O/1/I/L 这些手抄时容易认错的。</summary>
    private const string SafeCodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    private const string HostConnection = "conn-host";
    private const string GuestConnection = "conn-guest";

    // ---------------------------------------------------------------- 建房

    [Fact]
    public void StartSharingShouldCreateRoomWithSafeCode()
    {
        var registry = CreateRegistry();

        var room = registry.StartSharing(HostConnection, NewRequest(title: "Example", url: "https://example.com/a"));

        room.Id.Should().HaveLength(6);
        room.Id.Should().Match(code => code.All(SafeCodeAlphabet.Contains));
        room.Title.Should().Be("Example");
        room.Url.Should().Be("https://example.com/a");
        room.Host.Should().NotBeNullOrWhiteSpace();
        room.PeerCount.Should().Be(1);
        room.Subscribed.Should().BeFalse();
        room.LatencyMs.Should().BeNull();
        room.UpdatedAt.Should().BeGreaterThan(0);

        registry.RoomCount.Should().Be(1);
    }

    [Fact]
    public void StartSharingShouldEmitOnlySafeCharactersAcrossManyRooms()
    {
        var registry = CreateRegistry(settings => settings.MaxRooms = 200);

        for (var index = 0; index < 200; index++)
        {
            var room = registry.StartSharing($"conn-{index}", NewRequest(url: $"https://example.com/{index}"));
            room.Id.Should().Match(code => code.All(SafeCodeAlphabet.Contains), "房间号要能手抄，不能出现易混淆字符");
        }

        registry.RoomCount.Should().Be(200);
    }

    [Fact]
    public void StartSharingShouldBeIdempotentForTheSameConnection()
    {
        var registry = CreateRegistry();

        var first = registry.StartSharing(HostConnection, NewRequest(title: "First", url: "https://example.com/one"));
        var second = registry.StartSharing(HostConnection, NewRequest(title: "Second", url: "https://example.com/two"));

        // 同一个连接重复发起只更新共享目标，房间号不变（SPA 里换页就是这个场景）
        second.Id.Should().Be(first.Id);
        second.Title.Should().Be("Second");
        second.Url.Should().Be("https://example.com/two");
        registry.RoomCount.Should().Be(1);
    }

    [Theory]
    [InlineData("", "https://example.com")]
    [InlineData("   ", "https://example.com")]
    public void StartSharingShouldRejectMissingTitle(string title, string url)
    {
        var registry = CreateRegistry();

        var action = () => registry.StartSharing(HostConnection, NewRequest(title: title, url: url));

        action.Should().Throw<CustomException>().WithMessage("*title*");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///c:/tmp/a.html")]
    public void StartSharingShouldRejectNonHttpUrl(string url)
    {
        var registry = CreateRegistry();

        var action = () => registry.StartSharing(HostConnection, NewRequest(url: url));

        action.Should().Throw<CustomException>().WithMessage("*url*");
    }

    [Fact]
    public void StartSharingShouldRejectNullRequest()
    {
        var registry = CreateRegistry();

        var action = () => registry.StartSharing(HostConnection, null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void StartSharingShouldThrowWhenRoomCapacityIsReached()
    {
        var registry = CreateRegistry(settings => settings.MaxRooms = 1);
        registry.StartSharing(HostConnection, NewRequest());

        var action = () => registry.StartSharing("conn-other", NewRequest());

        action.Should().Throw<ConflictException>().WithMessage("*capacity*");
        registry.RoomCount.Should().Be(1);
    }

    // ---------------------------------------------------------------- 订阅

    [Fact]
    public void SubscribeShouldAddMemberAndReportAudience()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        var audience = registry.Subscribe(GuestConnection, room.Id.ToLowerInvariant(), "Guest");

        // 共享码大小写不敏感
        audience.RoomCode.Should().Be(room.Id);
        audience.ActorConnectionId.Should().Be(GuestConnection);
        audience.Peer.Should().NotBeNull();
        audience.Peer!.Name.Should().Be("Guest");
        audience.Peer.Color.Should().NotBeNullOrWhiteSpace();
        audience.Members.Should().HaveCount(2);
        audience.ConnectionIds.Should().BeEquivalentTo([HostConnection, GuestConnection]);
        audience.RoomClosed.Should().BeFalse();
    }

    [Fact]
    public void SubscribeShouldBeIdempotentAndNotNotifyAgain()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var again = registry.Subscribe(GuestConnection, room.Id, "Guest");

        // 已经在房间里：不再有需要通知的人，也不重复给身份
        again.ConnectionIds.Should().BeEmpty();
        again.Peer.Should().BeNull();
        again.Members.Should().HaveCount(2);

        registry.ListRooms(HostConnection).Single().PeerCount.Should().Be(2);
    }

    [Fact]
    public void SubscribeShouldThrowWhenRoomDoesNotExist()
    {
        var registry = CreateRegistry();

        var action = () => registry.Subscribe(GuestConnection, "ZZZZZZ", null);

        action.Should().Throw<NotFoundException>();
    }

    [Fact]
    public void SubscribeShouldAssignDistinctPeerColorsWithinRoom()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        // 调色板一共 8 色，正好铺满一整轮
        ShareAudience? last = null;
        for (var index = 0; index < 7; index++)
        {
            last = registry.Subscribe($"conn-{index}", room.Id, $"Guest-{index}");
        }

        last.Should().NotBeNull();
        last!.Members.Should().HaveCount(8);

        // 服务端负责保证同一房间内配色不重样
        last.Members.Select(peer => peer.Color).Should().OnlyHaveUniqueItems();
        last.Members.Select(peer => peer.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SubscribeShouldRespectPerConnectionSubscriptionLimit()
    {
        var registry = CreateRegistry(settings => settings.MaxSubscriptionsPerConnection = 1);
        var first = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/one"));
        var second = registry.StartSharing("conn-host-2", NewRequest(url: "https://example.com/two"));

        registry.Subscribe(GuestConnection, first.Id, "Guest");

        var action = () => registry.Subscribe(GuestConnection, second.Id, "Guest");

        action.Should().Throw<ConflictException>().WithMessage("*subscriptions*");
    }

    // ---------------------------------------------------------------- 退订

    [Fact]
    public void UnsubscribeShouldRemoveMember()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var audience = registry.Unsubscribe(GuestConnection, room.Id);

        audience.Should().NotBeNull();
        audience!.RoomCode.Should().Be(room.Id);
        audience.Peer.Should().NotBeNull();
        audience.Members.Should().HaveCount(1);
        audience.ConnectionIds.Should().BeEquivalentTo([HostConnection]);
    }

    [Fact]
    public void UnsubscribeShouldReturnNullWhenNotAMember()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        // 幂等：本来就不在房间里，不算失败
        registry.Unsubscribe(GuestConnection, room.Id).Should().BeNull();
        registry.Unsubscribe(GuestConnection, "ZZZZZZ").Should().BeNull();
    }

    [Fact]
    public void UnsubscribeShouldRejectTheHost()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        var action = () => registry.Unsubscribe(HostConnection, room.Id);

        // 房主要结束共享得走 StopSharing，否则房间会变成没人管的孤儿
        action.Should().Throw<ConflictException>();
    }

    [Fact]
    public void StopSharingShouldCloseRoomAndReturnNullForNonHost()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.StopSharing("conn-other").Should().BeNull();

        var audience = registry.StopSharing(HostConnection);

        audience.Should().NotBeNull();
        audience!.RoomClosed.Should().BeTrue();
        audience.Peer.Should().BeNull();
        audience.ConnectionIds.Should().BeEquivalentTo([HostConnection]);
        registry.RoomCount.Should().Be(0);
        registry.ListRooms(HostConnection).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- 发布 patch

    [Fact]
    public void PublishChangesShouldForwardToEveryoneButTheSender()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var result = registry.PublishChanges(HostConnection, NewPatch(room.Url, 2));

        result.Should().NotBeNull();
        result!.RoomCode.Should().Be(room.Id);
        result.ConnectionIds.Should().BeEquivalentTo([GuestConnection]);
        result.Rejected.Should().BeFalse();
        result.Patch.Ops.Should().HaveCount(2);
    }

    [Fact]
    public void PublishChangesShouldReturnNullWhenCallerIsNotHost()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        // 只有房主能推：订阅端只能看，不能改别人的页面
        registry.PublishChanges(GuestConnection, NewPatch(room.Url, 1)).Should().BeNull();
        registry.PublishChanges("conn-stranger", NewPatch(room.Url, 1)).Should().BeNull();
    }

    [Fact]
    public void PublishChangesShouldAdvanceTheRoomVersionSoTheNextBaseMatches()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        // 房间刚建好、还没有任何增量时，权威版本是 0 —— 第一份 patch 就该基于 0
        var first = registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 0));

        first!.Rejected.Should().BeFalse();
        first.Version.Should().Be(1);
        first.Patch.Version.Should().Be(1);

        // 第二份必须基于 1，否则它叠的是另一棵树
        var second = registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 1));

        second!.Rejected.Should().BeFalse();
        second.Version.Should().Be(2);
    }

    [Fact]
    public void PublishChangesShouldRejectStaleBaseInsteadOfForwardingIt()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 0));

        // 发送方以为房间还停在 0（比如上一条 patch 丢了），这条绝不能转发出去：
        // 里面的 insert / move 下标是按它自己那棵树算的，在别人的树上会静默落错位置。
        var stale = registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 0));

        stale.Should().NotBeNull();
        stale!.Rejected.Should().BeTrue();
        stale.ConnectionIds.Should().BeEmpty();

        // 被拒时把权威版本带回去 —— 房主靠它知道自己该对齐到哪儿
        stale.Version.Should().Be(1);

        // 版本没有因为一次失败的上报而前进
        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 1))!.Version.Should().Be(2);
    }

    [Fact]
    public void PublishChangesShouldRejectAPatchItCannotForwardIntactInsteadOfTrimmingIt()
    {
        var registry = CreateRegistry(settings =>
        {
            settings.MaxOpsPerPatch = 3;
            settings.MaxValueLength = 8;
        });

        var room = registry.StartSharing(HostConnection, NewRequest());

        // 五条操作，超过 MaxOpsPerPatch = 3
        var tooMany = registry.PublishChanges(HostConnection, new DomPatchDto
        {
            Url = room.Url,
            Ops =
            [
                new DomOpDto { Op = DomOpKinds.Remove, Id = 1 },
                new DomOpDto { Op = DomOpKinds.Remove, Id = 2 },
                new DomOpDto { Op = DomOpKinds.Remove, Id = 3 },
                new DomOpDto { Op = DomOpKinds.Remove, Id = 4 },
                new DomOpDto { Op = DomOpKinds.Remove, Id = 5 },
            ],
        });

        // 裁掉两条再转发 = 那两条的效果永远消失，而两端的版本号照涨、都以为自己是同步的。
        // 所以必须整条拒收（上一版这里是"转发了前 3 条"，那正是静默分叉）。
        tooMany!.Rejected.Should().BeTrue();
        tooMany.Reason.Should().Be(PatchRejectReasons.TooLarge);

        // **拒收不推进版本**：房间的坐标没动过，发送方对齐之后再发即可。
        // 版本要是推进了，发送方就再也对不上，只能一路重建基线。
        tooMany.Version.Should().Be(0);
        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 0))!
            .Rejected.Should().BeFalse();

        // 超长值同样拒收：截断会让接收方拿到一个发送方手里根本没有的字符串
        var longValue = registry.PublishChanges(HostConnection, new DomPatchDto
        {
            Url = room.Url,
            BaseVersion = 1,
            Ops =
            [
                new DomOpDto
                {
                    Op = DomOpKinds.Attrs,
                    Id = 1,
                    Set = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["title"] = new string('x', 100),
                    },
                },
            ],
        });

        longValue!.Rejected.Should().BeTrue();
        longValue.Reason.Should().Be(PatchRejectReasons.TooLarge);
    }

    [Fact]
    public void PublishChangesShouldRejectUnknownOpKindsLoudlyInsteadOfDroppingThem()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        var action = () => registry.PublishChanges(HostConnection, new DomPatchDto
        {
            Url = room.Url,
            Ops =
            [
                new DomOpDto { Op = "set-innerhtml", Id = 1, Text = "<b>whatever</b>" },
                new DomOpDto { Op = DomOpKinds.Remove, Id = 2 },
            ],
        });

        // 认不出的种类必须**响亮地炸出来**。
        //
        // 悄悄丢掉它，等于把 1 号那一步的效果从接收方的树上抹掉，而两端都以为自己是同步的。
        // 也不能"拒收了事"：发送方还会继续发它，重新交基线也救不回来，只会来回打转 ——
        // 它说明的是两端协议版本对不上，是 bug，不是可以自己恢复的状况。
        action.Should().Throw<CustomException>().WithMessage("*set-innerhtml*");
    }

    [Fact]
    public void PublishChangesShouldKeepTheInsertAnchorIntactInsteadOfGuessingAnIndex()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var result = registry.PublishChanges(HostConnection, new DomPatchDto
        {
            Url = room.Url,
            Ops =
            [
                // 锚点是"插到 id=42 之前"，必须原样带过去
                new DomOpDto
                {
                    Op = DomOpKinds.Insert,
                    Id = 5,
                    Next = 42,
                    Nodes = [new SerializedNodeDto { Id = 6, Type = SerializedNodeTypes.Element, Tag = "li" }],
                },
                // 没有锚点 = 追加到父节点末尾。**不能**顺手补成 0 —— 补成 0 会把它插到第一个孩子前面，
                // 页面不会报错、只是顺序不对，属于最难查的一类静默错误。
                new DomOpDto
                {
                    Op = DomOpKinds.Insert,
                    Id = 5,
                    Nodes = [new SerializedNodeDto { Id = 7, Type = SerializedNodeTypes.Element, Tag = "li" }],
                },
            ],
        });

        result!.Patch.Ops.Should().HaveCount(2);
        result.Patch.Ops[0].Next.Should().Be(42);
        result.Patch.Ops[1].Next.Should().BeNull();
    }

    [Fact]
    public void PublishChangesShouldKeepTheInsertedSubtreeSoNewNodesCanBeReplayed()
    {
        var registry = CreateRegistry();

        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var result = registry.PublishChanges(HostConnection, new DomPatchDto
        {
            Url = room.Url,
            Ops =
            [
                new DomOpDto
                {
                    Op = DomOpKinds.Insert,
                    Id = 5,
                    Next = 12,
                    Nodes =
                    [
                        new SerializedNodeDto
                        {
                            Id = 6,
                            Type = SerializedNodeTypes.Element,
                            Tag = "li",
                            Children = [new SerializedNodeDto { Id = 7, Type = SerializedNodeTypes.Text, Text = "hello" }],
                        },
                    ],
                },
                new DomOpDto
                {
                    Op = DomOpKinds.Insert,
                    Id = 5,
                    Next = 13,
                    Nodes =
                    [
                        new SerializedNodeDto { Id = 8, Type = SerializedNodeTypes.Element, Tag = "li" },
                        new SerializedNodeDto { Id = 9, Type = SerializedNodeTypes.Element, Tag = "li" },
                    ],
                },
            ],
        });

        result.Should().NotBeNull();

        // 这一段是"新增内容能不能被复现"的关键：必须**整棵**带上节点与 id
        // （id 是接收方唯一的身份来源，丢了 id 这个节点后续就再也指不准了）
        var first = result!.Patch.Ops[0];
        first.Nodes.Should().HaveCount(1);
        first.Nodes![0].Id.Should().Be(6);
        first.Nodes[0].Children![0].Text.Should().Be("hello");
        first.Next.Should().Be(12);

        var second = result.Patch.Ops[1];
        second.Nodes.Should().HaveCount(2);
        second.Nodes![1].Id.Should().Be(9);
    }

    [Fact]
    public void PublishChangesShouldRejectAnInsertWhoseSubtreeExceedsTheNodeBudget()
    {
        var registry = CreateRegistry(settings => settings.MaxInsertNodes = 2);

        var room = registry.StartSharing(HostConnection, NewRequest());

        var result = registry.PublishChanges(HostConnection, new DomPatchDto
        {
            Url = room.Url,
            Ops =
            [
                new DomOpDto
                {
                    Op = DomOpKinds.Insert,
                    Id = 5,
                    Nodes =
                    [
                        new SerializedNodeDto { Id = 8, Type = SerializedNodeTypes.Element, Tag = "li" },
                        new SerializedNodeDto { Id = 9, Type = SerializedNodeTypes.Element, Tag = "li" },
                        new SerializedNodeDto { Id = 10, Type = SerializedNodeTypes.Element, Tag = "li" },
                    ],
                },
            ],
        });

        // 裁到上限再转发的话，10 号那个节点永远不会出现在接收方的树上，
        // 而双方的版本号照涨 —— 又一处静默分叉。拒收才对，由发送方自己拆小重发。
        result!.Rejected.Should().BeTrue();
        result.Reason.Should().Be(PatchRejectReasons.TooLarge);
    }

    [Fact]
    public void PublishChangesShouldFillMissingEnvelopeFields()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/page"));
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var result = registry.PublishChanges(HostConnection, new DomPatchDto { Ops = [] });

        result.Should().NotBeNull();
        result!.Patch.Id.Should().NotBeNullOrWhiteSpace();
        result.Patch.Url.Should().Be("https://example.com/page");
        result.Patch.At.Should().BeGreaterThan(0);
        result.Patch.RoomCode.Should().Be(room.Id);
    }

    [Fact]
    public void PublishChangesShouldStampRoomCodeFromTheServerNotTheSender()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        var other = registry.StartSharing("conn-other-host", NewRequest());

        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var patch = NewPatch(room.Url, 1);
        // 发送方谎报房间号，必须被服务端覆盖 —— 否则可以把增量导到别人的房间里
        patch.RoomCode = other.Id;

        var result = registry.PublishChanges(HostConnection, patch);

        result.Should().NotBeNull();
        result!.Patch.RoomCode.Should().Be(room.Id);
    }

    [Fact]
    public void PublishChangesShouldStampVersionFromTheServerNotTheSender()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var patch = NewPatch(room.Url, 1, baseVersion: 0);
        // 发送方谎报"这会变成第 99 版"，接收方要是信了，本地版本就再也和房间对不上
        patch.Version = 99;

        var result = registry.PublishChanges(HostConnection, patch);

        result!.Patch.Version.Should().Be(1);
    }

    [Fact]
    public void PublishChangesShouldNotTouchRoomListVersion()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var versionBefore = registry.Version;

        // 房间内的持续活跃不该刷爆全局版本号，否则每个 patch 都让所有客户端重拉列表
        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 0));
        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 1));

        registry.Version.Should().Be(versionBefore);
    }

    // ---------------------------------------------------------------- 断线清理

    [Fact]
    public void RemoveConnectionShouldCloseHostedRoom()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var audiences = registry.RemoveConnection(HostConnection);

        var closed = audiences.Should().ContainSingle().Subject;
        closed.RoomClosed.Should().BeTrue();
        closed.RoomCode.Should().Be(room.Id);

        // 已经断开的连接不该再进投递名单
        closed.ConnectionIds.Should().BeEquivalentTo([GuestConnection]);
        registry.RoomCount.Should().Be(0);
    }

    [Fact]
    public void RemoveConnectionShouldLeaveSubscribedRoomsOnly()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var audiences = registry.RemoveConnection(GuestConnection);

        var left = audiences.Should().ContainSingle().Subject;
        left.RoomClosed.Should().BeFalse();
        left.RoomCode.Should().Be(room.Id);
        left.Peer.Should().NotBeNull();

        // 房还在，只是少了一个人
        registry.RoomCount.Should().Be(1);
        registry.ListRooms(HostConnection).Single().PeerCount.Should().Be(1);
    }

    [Fact]
    public void RemoveConnectionShouldBeNoOpForUnknownConnection()
    {
        var registry = CreateRegistry();

        registry.RemoveConnection("conn-nobody").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- 列表

    [Fact]
    public void ListRoomsShouldComputeSubscribedPerCaller()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        // subscribed 是按调用者各算各的，服务端不替客户端判断
        registry.ListRooms(HostConnection).Single().Subscribed.Should().BeFalse();
        registry.ListRooms(GuestConnection).Single().Subscribed.Should().BeTrue();
        registry.ListRooms("conn-stranger").Single().Subscribed.Should().BeFalse();
    }

    [Fact]
    public void VersionShouldAdvanceOnMembershipButNotOnPublish()
    {
        var registry = CreateRegistry();

        var version0 = registry.Version;

        var room = registry.StartSharing(HostConnection, NewRequest());
        var version1 = registry.Version;
        version1.Should().BeGreaterThan(version0);

        registry.Subscribe(GuestConnection, room.Id, "Guest");
        var version2 = registry.Version;
        version2.Should().BeGreaterThan(version1);

        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1));
        registry.Version.Should().Be(version2);

        registry.Unsubscribe(GuestConnection, room.Id);
        registry.Version.Should().BeGreaterThan(version2);
    }

    // ---------------------------------------------------------------- 我是谁

    [Fact]
    public void GetSelfShouldReturnHostPeerForTheHostConnection()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(name: "Owner"));

        var self = registry.GetSelf(HostConnection, room.Id);

        self.Should().NotBeNull();
        self!.Name.Should().Be("Owner");
        self.Id.Should().HaveLength(8);
        self.Color.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetSelfShouldReturnTheSameIdThatAppearsInThePeerList()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(name: "Owner"));
        var audience = registry.Subscribe(GuestConnection, room.Id, "Watcher");

        var hostSelf = registry.GetSelf(HostConnection, room.Id);
        var guestSelf = registry.GetSelf(GuestConnection, room.Id);

        hostSelf.Should().NotBeNull();
        guestSelf.Should().NotBeNull();

        // 关键不变量：PeersChanged 推给所有人的是同一份列表，
        // 客户端只能靠 GetSelf 拿到的 id 去列表里认领自己。
        audience.Members.Select(member => member.Id).Should().Contain(hostSelf!.Id);
        audience.Members.Select(member => member.Id).Should().Contain(guestSelf!.Id);
        hostSelf.Id.Should().NotBe(guestSelf.Id);
        hostSelf.Name.Should().Be("Owner");
        guestSelf.Name.Should().Be("Watcher");
    }

    [Fact]
    public void GetSelfShouldBeCaseInsensitiveOnRoomCode()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.GetSelf(HostConnection, room.Id.ToLowerInvariant()).Should().NotBeNull();
        registry.GetSelf(HostConnection, room.Id.ToUpperInvariant()).Should().NotBeNull();
    }

    [Fact]
    public void GetSelfShouldReturnNullWhenNotInTheRoom()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.GetSelf("conn-stranger", room.Id).Should().BeNull();
        registry.GetSelf(HostConnection, "ZZZZZZ").Should().BeNull();
        registry.GetSelf(string.Empty, room.Id).Should().BeNull();
        registry.GetSelf(HostConnection, string.Empty).Should().BeNull();
    }

    [Fact]
    public void GetSelfShouldReturnNullAfterLeavingOrRoomClosure()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Watcher");

        registry.Unsubscribe(GuestConnection, room.Id);
        registry.GetSelf(GuestConnection, room.Id).Should().BeNull();
        // 房主还在，不受影响
        registry.GetSelf(HostConnection, room.Id).Should().NotBeNull();

        registry.StopSharing(HostConnection);
        registry.GetSelf(HostConnection, room.Id).Should().BeNull();
    }

    // ---------------------------------------------------------------- 整页快照

    [Fact]
    public void GetSnapshotShouldReturnNullBeforeAnyonePublishes()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.GetSnapshot(HostConnection, room.Id).Should().BeNull();
    }

    [Fact]
    public void PublishSnapshotShouldStampServerFieldsAndNotifySubscribers()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/page"));
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var result = registry.PublishSnapshot(HostConnection, NewSnapshot(NewTree("hi")));

        result.Should().NotBeNull();
        result!.RoomCode.Should().Be(room.Id);

        // 订阅者名单里不能有房主自己：它是交基线的人，不需要再被通知去取
        result.SubscriberConnectionIds.Should().BeEquivalentTo([GuestConnection]);

        result.Snapshot.RoomCode.Should().Be(room.Id);
        result.Snapshot.Doctype.Should().Be("html");
        result.Snapshot.Truncated.Should().BeFalse();
        result.Snapshot.At.Should().BeGreaterThan(0);

        // 节点 id 必须原样保留 —— 它是后续所有增量指向节点的唯一凭据，服务端动了就全乱了
        result.Snapshot.Root.Id.Should().Be(1);
        result.Snapshot.Root.Children![0].Children![0].Children![0].Text.Should().Be("hi");

        // 地址以服务端记录的房间地址为准，不采信提交方
        result.Snapshot.Url.Should().Be("https://example.com/page");
    }

    [Fact]
    public void PublishSnapshotShouldStampRoomCodeFromTheServerNotTheSender()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        var other = registry.StartSharing("conn-other-host", NewRequest());

        var snapshot = NewSnapshot();
        snapshot.RoomCode = other.Id;
        snapshot.Url = "https://evil.example.com/";

        var result = registry.PublishSnapshot(HostConnection, snapshot);

        result!.Snapshot.RoomCode.Should().Be(room.Id);
        result.Snapshot.Url.Should().Be(room.Url);
    }

    [Fact]
    public void PublishSnapshotShouldBeRejectedForNonHost()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        // 基线决定观众看到什么内容，不能由非房主塞进来
        registry.PublishSnapshot(GuestConnection, NewSnapshot()).Should().BeNull();
        registry.PublishSnapshot("conn-stranger", NewSnapshot()).Should().BeNull();
        registry.GetSnapshot(HostConnection, room.Id).Should().BeNull();
    }

    [Fact]
    public void PublishSnapshotShouldRejectARootThatCannotCoverThePage()
    {
        var registry = CreateRegistry();
        registry.StartSharing(HostConnection, NewRequest());

        var textRoot = () => registry.PublishSnapshot(HostConnection, NewSnapshot(NewTextOnlyRoot()));
        var commentRoot = () => registry.PublishSnapshot(HostConnection, NewSnapshot(NewCommentRoot()));

        // 文本/注释当根都会被观众端重建出一片空白，属于调用方 bug，要炸出来而不是静默存下。
        // 判据必须是 Type 而不是"有没有 tagName" —— 注释同样没有 tagName，
        // 只凭 tagName 判断会把注释放过去。
        textRoot.Should().Throw<CustomException>().WithMessage("*root*");
        commentRoot.Should().Throw<CustomException>().WithMessage("*root*");
    }

    [Fact]
    public void SerializedNodeShouldUseRrwebFieldNamesOnTheWire()
    {
        // 这个测试守的是**字段名**，不是值。
        //
        // 节点树的字段名必须与 rrweb 的 serializedNodeWithId 完全一致 —— 观众端拿到之后是原样
        // 交给 rrweb 的 rebuild() 的。名字改了不会编译报错、也不会抛异常，只是线上少一个字段、
        // 观众端悄悄少一点东西，页面上还看不出是哪一步坏的。
        // （这不是假想：上一个协议里的 html 字段就是因为名字没对上被**静默丢掉**过一片。）
        var json = JsonSerializer.Serialize(
            new SerializedNodeDto
            {
                Id = 4,
                Type = SerializedNodeTypes.Element,
                Tag = "div",
                Attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "a" },
                Children = [new SerializedNodeDto { Id = 5, Type = SerializedNodeTypes.Text, Text = "hi" }],
            },
            WireJson);

        json.Should().Contain("\"id\":4");
        json.Should().Contain("\"type\":2");
        json.Should().Contain("\"tagName\":\"div\"");
        json.Should().Contain("\"attributes\":{");
        json.Should().Contain("\"childNodes\":[");
        json.Should().Contain("\"textContent\":\"hi\"");
    }

    [Fact]
    public void SerializedNodeShouldRoundTripThroughJson()
    {
        // 带 Optional 字段（RootId / IsSvg / CompatMode …）的节点必须能原样往返：
        // 这些字段在拷贝路径上是**逐个复制**的，漏掉一个只在观众端表现为"少了点东西"。
        var original = new SerializedNodeDto
        {
            Id = 1,
            Type = SerializedNodeTypes.Element,
            Tag = "svg",
            IsSvg = true,
            NeedBlock = true,
            IsCustom = true,
            RootId = 0,
            IsShadowHost = true,
            IsShadow = true,
            CompatMode = "BackCompat",
            Attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["viewBox"] = "0 0 1 1" },
            Children = [new SerializedNodeDto { Id = 2, Type = SerializedNodeTypes.Comment, Text = "<!-- c -->" }],
        };

        var restored = JsonSerializer.Deserialize<SerializedNodeDto>(JsonSerializer.Serialize(original, WireJson), WireJson)!;

        restored.Id.Should().Be(1);
        restored.Type.Should().Be(SerializedNodeTypes.Element);
        restored.Tag.Should().Be("svg");
        restored.IsSvg.Should().BeTrue();
        restored.NeedBlock.Should().BeTrue();
        restored.IsCustom.Should().BeTrue();
        restored.RootId.Should().Be(0);
        restored.IsShadowHost.Should().BeTrue();
        restored.IsShadow.Should().BeTrue();
        restored.CompatMode.Should().Be("BackCompat");
        restored.Attrs!["viewBox"].Should().Be("0 0 1 1");
        restored.Children![0].Type.Should().Be(SerializedNodeTypes.Comment);
        restored.Children[0].Text.Should().Be("<!-- c -->");
    }

    [Fact]
    public void PublishSnapshotShouldReplaceThePreviousSnapshot()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.PublishSnapshot(HostConnection, NewSnapshot(NewTree("first")));
        registry.PublishSnapshot(HostConnection, NewSnapshot(NewTree("second")));

        var stored = registry.GetSnapshot(HostConnection, room.Id)!;
        stored.Root.Children![0].Children![0].Children![0].Text.Should().Be("second");
    }

    [Fact]
    public void GetSnapshotShouldReturnACopySoLaterPublishesDoNotRewriteIt()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.PublishSnapshot(HostConnection, NewSnapshot(NewTree("first")));
        var taken = registry.GetSnapshot(HostConnection, room.Id)!;

        registry.PublishSnapshot(HostConnection, NewSnapshot(NewTree("second")));

        // 已经交出去的那份不能被后续写入改写 —— 所以 Clone 必须是深拷贝，
        // 只复制外层对象的话里面那棵树还是同一个引用，观众会拿到半新半旧的树
        taken.Root.Children![0].Children![0].Children![0].Text.Should().Be("first");
        taken.Root.Should().NotBeSameAs(registry.GetSnapshot(HostConnection, room.Id)!.Root);
    }

    [Fact]
    public void PublishSnapshotShouldPruneOversizedTreeAndFlagIt()
    {
        var registry = CreateRegistry(settings => settings.MaxSnapshotNodes = 3);
        var room = registry.StartSharing(HostConnection, NewRequest());

        // NewTree 有 4 个节点（html / body / div / 文本），上限 3 会把最后一个裁掉
        var result = registry.PublishSnapshot(HostConnection, NewSnapshot());

        result!.Snapshot.Truncated.Should().BeTrue();

        var kept = CountNodes(result.Snapshot.Root);
        kept.Should().BeLessThanOrEqualTo(3);

        // 根节点本身永远保留，否则观众端连文档都建不起来
        result.Snapshot.Root.Tag.Should().Be("html");
    }

    [Fact]
    public void PublishSnapshotShouldPruneOnCharacterBudgetNotJustNodeCount()
    {
        // 节点数给得很宽，只有字符预算能拦住它 —— 这一项才是真正兜住内存的那个数
        // （两万个节点每个都挂 4K 文本，光靠节点数上限是挡不住的）
        var registry = CreateRegistry(settings =>
        {
            settings.MaxSnapshotNodes = 1000;
            settings.MaxSnapshotChars = 100;
            settings.MaxValueLength = 4096;
        });

        registry.StartSharing(HostConnection, NewRequest());

        var result = registry.PublishSnapshot(HostConnection, NewSnapshot(NewWideTree()));

        result!.Snapshot.Truncated.Should().BeTrue();

        // 第一个 <p> 的内容就吃光了预算，第二个 <p> 连带上它的文本都不该进来
        var body = result.Snapshot.Root.Children![0];
        body.Children.Should().HaveCount(1);
        body.Children![0].Children![0].Text.Should().Be(new string('a', 100));
    }

    [Fact]
    public void PublishSnapshotShouldKeepSenderTruncationFlag()
    {
        var registry = CreateRegistry();
        registry.StartSharing(HostConnection, NewRequest());

        var snapshot = NewSnapshot();
        snapshot.Truncated = true;

        registry.PublishSnapshot(HostConnection, snapshot)!.Snapshot.Truncated.Should().BeTrue();
    }

    [Fact]
    public void SnapshotVersionShouldMatchTheRoomVersionItWasTakenAt()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 0));
        var second = registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 1))!;

        var snapshot = registry.PublishSnapshot(HostConnection, NewSnapshot())!;

        // 基线是在第 2 版之后抓的，所以它**代表**第 2 版：观众拿到它之后本地版本直接推到 2，
        // 只接受 baseVersion == 2 的下一份 patch。这就是"共同基线"的坐标。
        second.Version.Should().Be(2);
        snapshot.Snapshot.Version.Should().Be(2);

        // 基线之后发生的增量，版本必须严格更大，观众才会应用它
        var third = registry.PublishChanges(HostConnection, NewPatch(room.Url, 1, baseVersion: 2))!;
        third.Version.Should().Be(3);
    }

    [Fact]
    public void PublishChangesShouldKeepMonotonicVersionsPerRoom()
    {
        var registry = CreateRegistry();
        var first = registry.StartSharing(HostConnection, NewRequest());
        var second = registry.StartSharing("conn-other-host", NewRequest());

        registry.PublishChanges(HostConnection, NewPatch(first.Url, 1, baseVersion: 0))!.Version.Should().Be(1);
        registry.PublishChanges(HostConnection, NewPatch(first.Url, 1, baseVersion: 1))!.Version.Should().Be(2);

        // 版本是每个房间各算各的：另一个房间从 1 重新开始，两者互不影响
        registry.PublishChanges("conn-other-host", NewPatch(second.Url, 1, baseVersion: 0))!.Version.Should().Be(1);
    }

    [Fact]
    public void GetSnapshotShouldBeUnavailableOutsideTheRoom()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.PublishSnapshot(HostConnection, NewSnapshot());

        // 基线等于把被共享的页面整个交出去，不该让"知道房间号的陌生人"直接拉走
        registry.GetSnapshot(GuestConnection, room.Id).Should().BeNull();
        registry.GetSnapshot("conn-stranger", room.Id).Should().BeNull();
        registry.GetSnapshot(HostConnection, "ZZZZZZ").Should().BeNull();
        registry.GetSnapshot(HostConnection, "   ").Should().BeNull();

        // 订阅之后才取得到
        registry.Subscribe(GuestConnection, room.Id, "Guest");
        registry.GetSnapshot(GuestConnection, room.Id).Should().NotBeNull();
    }

    [Fact]
    public void RequestSnapshotShouldPointAtTheHostAndListSubscribers()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        var request = registry.RequestSnapshot(GuestConnection, room.Id);

        request.Should().NotBeNull();
        request!.RoomCode.Should().Be(room.Id);
        request.HostConnectionId.Should().Be(HostConnection);
        request.SubscriberConnectionIds.Should().BeEquivalentTo([GuestConnection]);
    }

    [Fact]
    public void RequestSnapshotShouldRequireMembership()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        registry.RequestSnapshot("conn-stranger", room.Id).Should().BeNull();
        registry.RequestSnapshot(GuestConnection, "ZZZZZZ").Should().BeNull();
    }

    [Fact]
    public void StartSharingShouldInvalidateSnapshotWhenTheTargetChanges()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/one"));
        registry.PublishSnapshot(HostConnection, NewSnapshot(NewTree("one")));

        // SPA 里换了共享目标：旧基线描述的是另一份页面，拿它当基线会复现出错误内容
        registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/two"));

        registry.GetSnapshot(HostConnection, room.Id).Should().BeNull();
    }

    [Fact]
    public void SnapshotShouldDisappearWithTheRoom()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.PublishSnapshot(HostConnection, NewSnapshot());

        registry.StopSharing(HostConnection);

        registry.GetSnapshot(HostConnection, room.Id).Should().BeNull();
    }

    // ---------------------------------------------------------------- 跳转（共享目标变化）

    [Fact]
    public void GetHostedRoomShouldReportTheCurrentTargetAndSubscribers()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/one"));

        var hosted = registry.GetHostedRoom(HostConnection);

        hosted.Should().NotBeNull();
        hosted!.Room.Id.Should().Be(room.Id);
        hosted.Room.Url.Should().Be("https://example.com/one");
        hosted.SubscriberConnectionIds.Should().BeEmpty();

        registry.Subscribe(GuestConnection, room.Id, "Guest");
        registry.GetHostedRoom(HostConnection)!.SubscriberConnectionIds.Should().BeEquivalentTo([GuestConnection]);

        // 重复发起（SPA 换页）房间号不变、地址变了 —— 调用方就是靠这两个值的差异
        // 判断"发生了一次跳转"，所以这里必须如实反映
        var updated = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/two"));

        updated.Id.Should().Be(room.Id);
        registry.GetHostedRoom(HostConnection)!.Room.Url.Should().Be("https://example.com/two");
    }

    [Fact]
    public void GetHostedRoomShouldBeNullWhenNotSharing()
    {
        var registry = CreateRegistry();

        registry.GetHostedRoom(HostConnection).Should().BeNull();
        registry.GetHostedRoom("   ").Should().BeNull();

        var room = registry.StartSharing(HostConnection, NewRequest());
        registry.GetHostedRoom(HostConnection).Should().NotBeNull();

        // 订阅者不是房主，没有"我在共享什么"这个概念
        registry.Subscribe(GuestConnection, room.Id, "Guest");
        registry.GetHostedRoom(GuestConnection).Should().BeNull();

        registry.StopSharing(HostConnection);
        registry.GetHostedRoom(HostConnection).Should().BeNull();
    }

    [Fact]
    public void NavigatingShouldKeepTheRoomAndItsSubscribers()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/one"));
        registry.Subscribe(GuestConnection, room.Id, "Guest");

        registry.StartSharing(HostConnection, NewRequest(url: "https://example.com/two"));

        // 跳转不关房间：房主还在，订阅关系、房间号、成员都要原样保留
        registry.RoomCount.Should().Be(1);
        registry.GetSelf(GuestConnection, room.Id).Should().NotBeNull();
        registry.GetHostedRoom(HostConnection)!.SubscriberConnectionIds.Should().BeEquivalentTo([GuestConnection]);
    }

    /// <summary>
    /// 空的 <c>childNodes</c> 必须原样活着走到线上。
    ///
    /// rrweb 的 <c>buildNodeWithSN()</c> 对文档与元素是**无条件**
    /// <c>for (const childN of n.childNodes)</c> 的（源码里没有 <c>if</c> 兜底），字段一旦缺失就是
    /// <c>TypeError: n.childNodes is not iterable</c>，**整份基线一个字都建不出来** ——
    /// 页面上随便一个 &lt;meta&gt; / &lt;br&gt; / &lt;img&gt; 就够触发，是必现而不是边缘情况。
    ///
    /// 采集端为此恒给这个字段（没有子节点就给空数组）。这条测试盯的是**裁剪那一层**：
    /// 它原来把空列表归一成了 null，于是 JSON 里整个键消失 —— 服务端亲手把发送方守住的
    /// 不变量拆掉，而症状出现在客户端，服务端日志里一个字都看不到。
    /// </summary>
    [Fact]
    public void PublishSnapshotShouldKeepAnEmptyChildNodesArrayOnTheWire()
    {
        var registry = CreateRegistry();
        var room = registry.StartSharing(HostConnection, NewRequest());

        // <html><head><meta charset="utf-8"></head></html> —— meta 没有子节点，采集端给它 childNodes: []
        var root = new SerializedNodeDto
        {
            Id = 1,
            Type = SerializedNodeTypes.Document,
            CompatMode = "CSS1Compat",
            Children =
            [
                new SerializedNodeDto
                {
                    Id = 2,
                    Type = SerializedNodeTypes.Element,
                    Tag = "html",
                    Children =
                    [
                        new SerializedNodeDto
                        {
                            Id = 3,
                            Type = SerializedNodeTypes.Element,
                            Tag = "head",
                            Children =
                            [
                                new SerializedNodeDto
                                {
                                    Id = 4,
                                    Type = SerializedNodeTypes.Element,
                                    Tag = "meta",
                                    Attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["charset"] = "utf-8" },
                                    Children = [],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        registry.PublishSnapshot(HostConnection, NewSnapshot(root));

        var stored = registry.GetSnapshot(HostConnection, room.Id);
        stored.Should().NotBeNull();

        var meta = stored!.Root!.Children![0].Children![0].Children![0];
        meta.Children.Should().NotBeNull("rrweb 无条件迭代 childNodes，字段缺失会让整份基线建不出来");
        meta.Children.Should().BeEmpty();

        // 真正要盯的是**线上 JSON**：字段到底在不在，只有序列化一遍才知道
        var json = JsonSerializer.Serialize(meta, WireJson);
        json.Should().Contain(
            "\"childNodes\":[]",
            "缺键与空数组在 rrweb 那边是两回事：前者直接 TypeError，后者才是正常的空元素");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// 与 SignalR 默认 JSON 协议一致的命名策略：camelCase，带 <c>JsonPropertyName</c> 的以特性为准。
    /// 用它来断言"线上到底长什么样"，而不是靠记忆。
    /// </summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static DomSnapshotDto NewSnapshot(SerializedNodeDto? root = null) => new()
    {
        Title = "Example Page",
        Url = "https://example.com/page",
        Doctype = "html",
        Root = root ?? NewTree(),
        At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    /// <summary>一棵最小的合规基线树：<c>&lt;html&gt;&lt;body&gt;&lt;div id="a"&gt;hello&lt;/div&gt;&lt;/body&gt;&lt;/html&gt;</c>。</summary>
    private static SerializedNodeDto NewTree() => new()
    {
        Id = 1,
        Type = SerializedNodeTypes.Element,
        Tag = "html",
        Children =
        [
            new SerializedNodeDto
            {
                Id = 2,
                Type = SerializedNodeTypes.Element,
                Tag = "body",
                Children =
                [
                    new SerializedNodeDto
                    {
                        Id = 3,
                        Type = SerializedNodeTypes.Element,
                        Tag = "div",
                        Attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "a" },
                        Children = [new SerializedNodeDto { Id = 4, Type = SerializedNodeTypes.Text, Text = "hello" }],
                    },
                ],
            },
        ],
    };

    /// <summary>同上，但把最深处的文本换成指定内容 —— 用来区分两份不同的基线。</summary>
    private static SerializedNodeDto NewTree(string text)
    {
        var tree = NewTree();
        tree.Children![0].Children![0].Children![0].Text = text;
        return tree;
    }

    /// <summary>数一棵树有多少个节点（含根）。</summary>
    private static int CountNodes(SerializedNodeDto node) =>
        1 + (node.Children?.Sum(CountNodes) ?? 0);

    /// <summary>一棵"节点不多但文本很重"的树：两个各带 100 字符文本的兄弟节点。</summary>
    private static SerializedNodeDto NewWideTree() => new()
    {
        Id = 1,
        Type = SerializedNodeTypes.Element,
        Tag = "html",
        Children =
        [
            new SerializedNodeDto
            {
                Id = 2,
                Type = SerializedNodeTypes.Element,
                Tag = "body",
                Children =
                [
                    new SerializedNodeDto
                    {
                        Id = 3,
                        Type = SerializedNodeTypes.Element,
                        Tag = "p",
                        Children = [new SerializedNodeDto { Id = 4, Type = SerializedNodeTypes.Text, Text = new string('a', 100) }],
                    },
                    new SerializedNodeDto
                    {
                        Id = 5,
                        Type = SerializedNodeTypes.Element,
                        Tag = "p",
                        Children = [new SerializedNodeDto { Id = 6, Type = SerializedNodeTypes.Text, Text = new string('b', 100) }],
                    },
                ],
            },
        ],
    };

    /// <summary>一条不合规的根节点：只有文本、没有标签 —— 文本没法当整页的基线。</summary>
    private static SerializedNodeDto NewTextOnlyRoot() => new()
    {
        Id = 7,
        Type = SerializedNodeTypes.Text,
        Text = "not a root",
    };

    /// <summary>
    /// 另一条不合规的根节点：注释。
    ///
    /// 这条是 <see cref="SerializedNodeDto.Type"/> 存在的意义所在 —— 注释和文本在 rrweb 的形状里
    /// 只靠 type 区分（两者的 textContent 都是字符串），所以**不能**再用"有没有 tagName"当判据，
    /// 否则注释会被当成文本放过去，观众端收到的是一棵画不出任何东西的树。
    /// </summary>
    private static SerializedNodeDto NewCommentRoot() => new()
    {
        Id = 8,
        Type = SerializedNodeTypes.Comment,
        Text = "<!-- not a root either -->",
    };

    private static InMemoryShareRegistry CreateRegistry(Action<ShareHubSettings>? configure = null)
    {
        var settings = new ShareHubSettings();
        configure?.Invoke(settings);

        return new InMemoryShareRegistry(
            Options.Create(settings),
            NullLogger<InMemoryShareRegistry>.Instance);
    }

    private static ShareStartRequest NewRequest(
        string title = "Example Page",
        string url = "https://example.com/page",
        string? name = null) => new()
        {
            Title = title,
            Url = url,
            // 这个 helper 也用来造非法 URL 的入参，所以不能假设 url 一定能解析
            Host = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : string.Empty,
            Name = name,
        };

    private static DomPatchDto NewPatch(string url, int opCount, long baseVersion = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Url = url,
        BaseVersion = baseVersion,
        At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Ops = Enumerable.Range(0, opCount)
            .Select(index => new DomOpDto
            {
                Op = DomOpKinds.Attrs,
                Id = index + 1,
                Set = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["data-value"] = index.ToString(),
                },
            })
            .ToList(),
    };
}
