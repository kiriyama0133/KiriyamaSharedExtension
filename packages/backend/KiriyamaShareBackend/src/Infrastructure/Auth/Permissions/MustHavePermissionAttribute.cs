using KiriyamaShareBackend.Shared.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace KiriyamaShareBackend.Infrastructure.Auth.Permissions;

public class MustHavePermissionAttribute : AuthorizeAttribute
{
    public MustHavePermissionAttribute(string action, string resource)
        => Policy = GNXPermission.NameFor(action, resource);
}