namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 被共享页面的**整页基线**：一棵带 id 的节点树，加上它代表哪个版本。
///
/// 旧版本这里是 `<c>&lt;html&gt;</c> 的 outerHTML 字符串`，配合选择器路径当增量。
/// 那种做法有两个绕不开的问题，都已经被实际踩到：
///   1. HTML 字符串里的节点没有身份，接收方只能靠重新解析出来的**位置**去对，而位置会随渲染漂移；
///   2. 字符串一旦截断就可能断在标签中间，解析器会"修补"出一棵和发送方不一样树，
///      而修补是静默的 —— 接收方从此在一棵错的树上叠所有后续增量。
/// 换成结构化节点树之后，整棵树的身份（<see cref="SerializedNodeDto.Id"/>）由发送方一次给定，
/// 接收方不解析、不推断，只重建，所以两端从一开始就是同一棵树。
///
/// 与前端 <c>types/index.ts</c> 的 <c>DomSnapshot</c> 对应。
/// </summary>
public class DomSnapshotDto
{
    /// <summary>所属房间号。由服务端盖章，发送方填什么都会被覆盖。</summary>
    public string RoomCode { get; set; } = string.Empty;

    /// <summary>快照对应的页面地址。以服务端记录的房间地址为准。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>页面标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 文档类型串（标准模式是 <c>html</c>，怪异模式为空串）。
    ///
    /// 必须单独带：它决定接收方用哪种模式渲染，而怪异模式和标准模式的布局可以差出一整页。
    /// 它不在节点树里 —— doctype 是文档级的、不是某个节点的属性。
    /// </summary>
    public string Doctype { get; set; } = "html";

    /// <summary>根节点（<c>&lt;html&gt;</c>）。</summary>
    public SerializedNodeDto Root { get; set; } = new();

    /// <summary>
    /// 这份快照**代表的房间版本**：应用完版本号小于等于它的 patch 之后，状态就等于这棵树。
    ///
    /// 有了它就精确了：接收方拿到快照后本地版本直接推到它，只接受 <c>baseVersion</c>
    /// 正好等于这个值的 patch。旧方案的序号只能比较"大于"，重复投递要靠猜，
    /// 而这是可以算准的。
    /// </summary>
    public long Version { get; set; }

    /// <summary>是否因为超过上限被裁剪过。裁剪后的页面必然不完整，客户端据此给出提示。</summary>
    public bool Truncated { get; set; }

    /// <summary>快照生成时间，epoch 毫秒。</summary>
    public long At { get; set; }
}
