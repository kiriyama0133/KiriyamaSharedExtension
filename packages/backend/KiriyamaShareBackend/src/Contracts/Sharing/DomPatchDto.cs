namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 一个 patch：作用在房间内某个**已知版本**上的一组 DOM 操作。
/// 与前端 <c>types/index.ts</c> 的 <c>DomPatch</c> 对应。
///
/// 这是整个协议的核心，形式上跟 Git 的 patch 一样：
///   - 不发全量（全量只在**建立基线**时发一次，见 <see cref="DomSnapshotDto"/>）；
///   - 不发"原始事件"（那条路两端对同一份 DOM 的理解会不断漂移）；
///   - 发的是"基于 base 版本的增量"，并且**base 必须对得上**。
///
/// base 对不上时服务端会拒收（见 <c>SharePublishResult</c>）。拒收不是失败，而是发现：
/// 一旦某个 patch 丢了，后面所有增量都会叠在一棵错的树上，页面会静默地越走越偏。
/// 与其让它偏下去，不如立刻暴露出来重新建立基线。
/// </summary>
public class DomPatchDto
{
    /// <summary>patch id，采集侧生成，仅用于日志排查。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>变更发生的页面地址。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 这批操作属于哪个房间。
    ///
    /// 由**服务端**在转发时盖章（发送方填什么都会被覆盖）：一个连接可以同时订阅多个房间，
    /// 客户端收到 patch 时必须知道它属于哪个房间，否则没法把增量还原到正确的目标上。
    /// </summary>
    public string RoomCode { get; set; } = string.Empty;

    /// <summary>
    /// **发送方认为**这个 patch 基于哪个版本。必须等于房间的当前版本才会被接受。
    /// 这是"共同基线"的检查点：不等就说明中间丢了 patch（或者发送方自己的状态已经跑偏）。
    /// </summary>
    public long BaseVersion { get; set; }

    /// <summary>
    /// 接受之后房间的新版本，由**服务端**分配（发送方填什么都会被覆盖）。
    /// 接收方应用成功后把本地版本推进到这个值。
    /// </summary>
    public long Version { get; set; }

    /// <summary>本次操作，按顺序应用。</summary>
    public List<DomOpDto> Ops { get; set; } = new();

    /// <summary>打包时间，epoch 毫秒。</summary>
    public long At { get; set; }
}
