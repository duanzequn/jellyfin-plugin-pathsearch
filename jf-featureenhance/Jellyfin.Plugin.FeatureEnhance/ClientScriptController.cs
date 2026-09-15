using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Serves the client side scripts (embedded resources) to the web app.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("FeatureEnhance")]
public class ClientScriptController : ControllerBase
{
    private const string ResourcePrefix = "Jellyfin.Plugin.FeatureEnhance.Web.";

    private static readonly IReadOnlyDictionary<string, string> Scripts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["grid.js"] = ResourcePrefix + "grid.js",
            ["results.js"] = ResourcePrefix + "results.js",
            ["smooth.js"] = ResourcePrefix + "smooth.js",
        };

    /// <summary>
    /// Returns one of the injected client scripts.
    /// </summary>
    /// <param name="name">Script name.</param>
    /// <returns>JavaScript.</returns>
    [HttpGet("Client/{name}")]
    public ActionResult GetScript([FromRoute] string name)
    {
        if (!Scripts.TryGetValue(name, out var resource))
        {
            return NotFound();
        }

        var stream = typeof(ClientScriptController).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);

        // 注入的 <script> 带 ?v=<进程启动时间戳>：Jellyfin 一重启 URL 就变，
        // 所以可以放心长缓存（之前是 no-cache，每次页面加载都要重新下 69KB 脚本）。
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Content(reader.ReadToEnd(), "text/javascript; charset=utf-8");
    }
}
