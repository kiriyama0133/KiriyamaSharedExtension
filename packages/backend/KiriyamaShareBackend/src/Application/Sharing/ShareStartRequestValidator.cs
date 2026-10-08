using KiriyamaShareBackend.Shared.Sharing;

namespace KiriyamaShareBackend.Application.Sharing;

/// <summary>
/// 发起共享的入参校验。
///
/// 走模板既有的 FluentValidation 约定（继承 <see cref="Common.Validation.CustomValidator{T}"/>，
/// 由 <c>Application.Startup</c> 的 <c>AddValidatorsFromAssembly</c> 自动注册），
/// 调用点在 <c>ShareHub.StartSharing</c> —— 校验放在传输层边界，和 MediatR 用
/// <c>ValidationBehavior</c> 在边界拦一道是同一个位置。
///
/// 为什么不放进 <c>InMemoryShareRegistry</c>：那个注册表是单例，而这些 validator 是 scoped
/// （模板里的其它 validator 会依赖 scoped 的 service），往单例里注入 scoped 会在启动时报错。
///
/// 长度上限是为了兜住匿名调用 —— 房间不需要登录，不能让人塞一个巨大的标题进内存。
/// </summary>
public class ShareStartRequestValidator : CustomValidator<ShareStartRequest>
{
    /// <summary>页面标题上限。</summary>
    public const int MaxTitleLength = 200;

    /// <summary>主机名上限。</summary>
    public const int MaxHostLength = 256;

    /// <summary>显示名上限。</summary>
    public const int MaxNameLength = 64;

    /// <summary>Initializes a new instance of the <see cref="ShareStartRequestValidator"/> class.</summary>
    public ShareStartRequestValidator()
    {
        RuleFor(request => request.Title)
            .NotEmpty()
                .WithMessage("Page title is required.")
            .MaximumLength(MaxTitleLength);

        RuleFor(request => request.Url)
            .NotEmpty()
            .Must(BeAbsoluteHttpUrl)
                .WithMessage("Page url must be an absolute http/https url.");

        RuleFor(request => request.Host)
            .MaximumLength(MaxHostLength);

        RuleFor(request => request.Name)
            .MaximumLength(MaxNameLength);
    }

    private static bool BeAbsoluteHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
