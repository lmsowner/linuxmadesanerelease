// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Microsoft.AspNetCore.Http;

namespace LinuxMadeSane.Web.Services;

public sealed class TerminalWorkspaceAccessor(IHttpContextAccessor httpContextAccessor)
{
    public const string CookieName = "lms.terminal.workspace";
    public const string HttpContextItemKey = "LmsTerminalWorkspaceId";

    public string GetWorkspaceId()
    {
        var context = httpContextAccessor.HttpContext;
        if (context?.Items[HttpContextItemKey] is string workspaceId && !string.IsNullOrWhiteSpace(workspaceId))
        {
            return workspaceId;
        }

        if (context?.Request.Cookies.TryGetValue(CookieName, out var cookieValue) == true &&
            !string.IsNullOrWhiteSpace(cookieValue))
        {
            return cookieValue;
        }

        return Guid.NewGuid().ToString("N");
    }
}
