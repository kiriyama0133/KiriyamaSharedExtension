using Asp.Versioning;

namespace KiriyamaShareBackend.WebApi.Controllers;

[Route("api/[controller]")]
[ApiVersionNeutral]
public class VersionNeutralApiController : BaseApiController
{
}
