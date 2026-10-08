namespace KiriyamaShareBackend.Shared.Sharing;

/// <summary>
/// 房间生命周期事件的种类。取值与前端 <c>types/index.ts</c> 的 <c>ActivityKind</c> 对齐，
/// 客户端拿到 kind 直接决定图标与文案。
/// </summary>
public static class ShareEventKinds
{
    /// <summary>开始共享。</summary>
    public const string Share = "share";

    /// <summary>有人订阅进来了。</summary>
    public const string Join = "join";

    /// <summary>有人取消订阅或掉线了。</summary>
    public const string Leave = "leave";

    /// <summary>
    /// 被共享的页面换了地址（SPA 路由跳转、或整页跳转后重新接上）。
    ///
    /// 房间**不会**因此关闭：房主还在，只是共享的目标换了。客户端收到它会去取一份新的整页快照
    /// 当作新基线 —— 旧基线描述的是另一个页面，继续往上叠增量只会越叠越错。
    /// </summary>
    public const string Navigate = "navigate";

    /// <summary>系统消息，目前用于"房间已关闭"。</summary>
    public const string System = "system";
}
