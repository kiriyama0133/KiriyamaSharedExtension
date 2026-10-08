using FluentAssertions;
using KiriyamaShareBackend.Application.Sharing;
using KiriyamaShareBackend.Shared.Sharing;
using Xunit;

namespace KiriyamaShareBackend.Infrastructure.Tests.Sharing;

/// <summary>
/// <see cref="ShareStartRequestValidator"/> 的规则测试。
/// 这个校验器跑在传输层边界（<c>ShareHub.StartSharing</c>），是匿名调用进来的第一道闸。
/// </summary>
public class ShareStartRequestValidatorTests
{
    private readonly ShareStartRequestValidator _validator = new();

    [Fact]
    public void ShouldAcceptAWellFormedRequest()
    {
        var result = _validator.Validate(NewRequest());

        result.IsValid.Should().BeTrue(string.Join(" | ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldRejectMissingTitle(string title)
    {
        var result = _validator.Validate(NewRequest(title: title));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ShareStartRequest.Title));
    }

    [Fact]
    public void ShouldRejectOverlongTitle()
    {
        var result = _validator.Validate(NewRequest(title: new string('x', ShareStartRequestValidator.MaxTitleLength + 1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ShareStartRequest.Title));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///tmp/page.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void ShouldRejectNonHttpUrl(string url)
    {
        var result = _validator.Validate(NewRequest(url: url));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ShareStartRequest.Url));
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://localhost:3000/a?b=c")]
    public void ShouldAcceptHttpAndHttpsUrls(string url)
    {
        var result = _validator.Validate(NewRequest(url: url));

        result.IsValid.Should().BeTrue(string.Join(" | ", result.Errors.Select(error => error.ErrorMessage)));
    }

    [Fact]
    public void ShouldRejectOverlongName()
    {
        var result = _validator.Validate(NewRequest(name: new string('n', ShareStartRequestValidator.MaxNameLength + 1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ShareStartRequest.Name));
    }

    [Fact]
    public void ShouldAllowEmptyNameBecauseServerAssignsOne()
    {
        var result = _validator.Validate(NewRequest(name: null));

        result.IsValid.Should().BeTrue(string.Join(" | ", result.Errors.Select(error => error.ErrorMessage)));
    }

    private static ShareStartRequest NewRequest(
        string title = "Example Page",
        string url = "https://example.com/page",
        string? name = null) => new()
        {
            Title = title,
            Url = url,
            Host = "example.com",
            Name = name,
        };
}
