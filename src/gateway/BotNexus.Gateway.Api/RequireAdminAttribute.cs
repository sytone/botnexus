using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Action filter that restricts access to admin callers only.
/// Reads the <see cref="GatewayCallerIdentity"/> set by <see cref="GatewayAuthMiddleware"/>
/// and returns 403 Forbidden if the caller is not an admin.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireAdminAttribute : Attribute, IActionFilter
{
    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var identity = context.HttpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey] as GatewayCallerIdentity;

        // Identity missing means the middleware did not set it (e.g. auth is disabled in dev mode).
        // Treat missing identity as non-admin.
        if (identity is null || !identity.IsAdmin)
        {
            context.Result = new ObjectResult(new { error = "forbidden", message = "Admin scope required." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context) { }
}
