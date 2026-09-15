using System;
using System.Security.Claims;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// 认证票据里的用户身份。
///
/// 插件不引用 Jellyfin.Api（那边才有 ClaimsPrincipalExtensions.GetUserId），所以这里直接读
/// 同名 claim —— Jellyfin.Api.Constants.InternalClaimTypes.UserId 就是 "Jellyfin-UserId"。
/// 关键点：身份**只能**来自票据，绝不能来自查询参数，否则任何登录用户都能冒充别人
/// （不传就是"全库"，传别人的 id 就按别人的权限算）。
/// </summary>
internal static class PluginUser
{
    private const string UserIdClaimType = "Jellyfin-UserId";

    /// <summary>
    /// 当前登录用户 id；API key（票据里没有用户 id）返回 <see cref="Guid.Empty"/>。
    /// </summary>
    /// <param name="principal">ControllerBase.User.</param>
    /// <returns>User id or <see cref="Guid.Empty"/>.</returns>
    internal static Guid GetUserId(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirst(UserIdClaimType)?.Value;
        return !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
