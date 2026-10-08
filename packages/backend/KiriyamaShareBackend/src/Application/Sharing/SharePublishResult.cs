using KiriyamaShareBackend.Shared.Sharing;

namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 一次 patch 的中转结果。
/// </summary>
public sealed record SharePublishResult
{
    /// <summary>所属房间。</summary>
    public string RoomCode { get; init; } = string.Empty;

    /// <summary>接收方连接 id（不含发起方）。空数组表示这条 patch 除了房主没别人在看。</summary>
    public string[] ConnectionIds { get; init; } = [];

    /// <summary>服务端处理后的 patch（补齐房间号、版本、时间戳）。被拒绝时是原样的输入。</summary>
    public DomPatchDto Patch { get; init; } = new();

    /// <summary>
    /// **拒收了。**两种原因，处理方式不同，见 <see cref="Reason"/>。
    ///
    /// 共同点是：**它绝不能**被当成增量转发出去。接收方会在错误的基线上应用这些操作，
    /// 或者拿到一组被裁剪过的操作，页面从此静默错位。
    /// 发送方收到这个标志后应当重新交一份整页基线来对齐（见 <see cref="Version"/>）。
    /// </summary>
    public bool Rejected { get; init; }

    /// <summary>
    /// 被拒的原因，取值见 <see cref="PatchRejectReasons"/>；被接受时是空串。
    /// 发送方要靠它区分"重建基线"（base 过期 / 太大）和"清掉房间状态"（已经不是房主）。
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// 房间的当前版本。被接受时是刚分配的新版本；被拒绝时是发送方需要重新对齐到的那个版本。
    /// </summary>
    public long Version { get; init; }
}
