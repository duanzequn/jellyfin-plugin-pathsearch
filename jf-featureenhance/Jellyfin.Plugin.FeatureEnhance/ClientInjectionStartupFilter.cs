using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Injects the Feature Enhance client scripts into jellyfin-web's index.html at request time.
/// Pure plugin: no files on disk are touched, nothing in the web folder is rewritten, so
/// Jellyfin upgrades cannot invalidate it.
/// </summary>
public class ClientInjectionStartupFilter : IStartupFilter
{
    private const string Marker = "FeatureEnhance/Client/";

    /// <summary>
    /// Scripts injected into index.html, in load order.
    /// </summary>
    private static readonly string[] Scripts =
    [
        "results.js",
        "grid.js",
        "smooth.js",
    ];

    /// <summary>
    /// 同步注入到 &lt;head&gt; 里的引导脚本（必须早于 app 的 bundle）。
    /// </summary>
    /// <remarks>
    /// 搜索页原本会发一堆 /Items?…&amp;searchTerm=… 的请求（限 800 条的那种），
    /// 结果全被我们自己的方格 UI 取代了 —— 之前只是"把渲染结果隐藏"，请求照样打、
    /// 800 条照样传，纯浪费。这里在最早的时刻包住 window.fetch / XMLHttpRequest：
    /// 匹配 searchTerm 的 /Items 请求直接回一个空结果（不发网络），
    /// 于是服务端的搜索、几百 KB 的 JSON、几百张卡片的构建全都不再发生。
    ///
    /// 这是**永久**短路：按插件所有者的要求，搜索页完全由插件接管，不存在任何自动或手动的
    /// "退回原生搜索"路径（接口挂了就显示 0 条 + 重试，见 search-results.js 的 renderError）。
    /// window.__feNativeSearch 只在调试时可以从 devtools 手动置 'on'（插件自己永不修改它，
    /// 也没人读它来决定行为），另外提供 __feNativeSearchKilled() 用来数拦截了多少条请求。
    /// </remarks>
    private const string Bootstrap =
        """
        <script>
        (function(){
        var empty=function(){return JSON.stringify({Items:[],TotalRecordCount:0,StartIndex:0});};
        function shouldKill(u){
        if(window.__feNativeSearch!=='off')return false;
        if(typeof u!=='string')return false;
        if(u.indexOf('searchTerm=')===-1)return false;
        if(u.indexOf('Ids=')!==-1)return false;
        if(u.indexOf('FeatureEnhance')!==-1)return false;
        return u.indexOf('/Items?')!==-1||u.indexOf('/Artists?')!==-1||u.indexOf('/Persons?')!==-1||u.indexOf('/Studios?')!==-1;
        }
        var killed=0;window.__feNativeSearch='off';window.__feNativeSearchKilled=function(){return killed;};
        var of=window.fetch;
        if(typeof of==='function'){window.fetch=function(i,n){try{var u=typeof i==='string'?i:((i&&i.url)||'');
        if(shouldKill(u)){killed++;var h={};h['Content-Type']='application/json;charset=utf-8';
        return Promise.resolve(new Response(empty(),{status:200,headers:h}));}}catch(e){}
        return of.apply(this,arguments);};}
        var proto=window.XMLHttpRequest&&window.XMLHttpRequest.prototype;
        if(proto){var oo=proto.open,os=proto.send,oh=proto.getAllResponseHeaders;
        proto.open=function(m,u){try{this.__feUrl=u;}catch(e){}return oo.apply(this,arguments);};
        proto.getAllResponseHeaders=function(){try{if(this.__feFake)return 'content-type: application/json; charset=utf-8\r\n';}catch(e){}return oh.apply(this,arguments);};
        proto.send=function(){var x=this;
        try{if(!shouldKill(x.__feUrl))return os.apply(this,arguments);
        killed++;x.__feFake=true;var body=empty();
        Object.defineProperty(x,'readyState',{configurable:true,get:function(){return 4;}});
        Object.defineProperty(x,'status',{configurable:true,get:function(){return 200;}});
        Object.defineProperty(x,'statusText',{configurable:true,get:function(){return 'OK';}});
        Object.defineProperty(x,'responseText',{configurable:true,get:function(){return body;}});
        Object.defineProperty(x,'response',{configurable:true,get:function(){try{return x.responseType==='json'?JSON.parse(body):body;}catch(e){return body;}}});
        setTimeout(function(){
        try{if(typeof x.onreadystatechange==='function')x.onreadystatechange();}catch(e){}
        try{x.dispatchEvent(new Event('readystatechange'));}catch(e){}
        try{if(typeof x.onload==='function')x.onload();}catch(e){}
        try{x.dispatchEvent(new Event('load'));}catch(e){}
        try{x.dispatchEvent(new Event('loadend'));}catch(e){}
        },0);}catch(e){return os.apply(this,arguments);}};}
        })();
        </script>
        """;

    private static readonly string _startedAt = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly ILogger<ClientInjectionStartupFilter> _logger;
    private int _loggedOnce;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ClientInjectionStartupFilter(ILogger<ClientInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Registered before the rest of the pipeline so it runs outermost; stripping
            // Accept-Encoding makes the static file handler return plain, uncompressed HTML.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!IsIndexRequest(context.Request.Path.Value) || !HttpMethods.IsGet(context.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        // 关键：必须连条件请求头一起去掉。
        // 静态文件中间件是**在响应开始时**才落 ETag/Last-Modified 的（实测：响应里这两个头
        // 清不掉），所以只要浏览器带着 If-None-Match/If-Modified-Since 过来，服务端就会回
        // 304 + 空 body —— 我们拿不到 HTML，注入整个被跳过，浏览器继续用它自己缓存里的那份
        // index.html。装插件之前访问过 Web UI 的浏览器缓存里没有注入，于是"插件时灵时不灵"。
        // 去掉这两个请求头，静态文件中间件只能回 200 全量，注入就一定会发生。
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;

        buffer.Seek(0, SeekOrigin.Begin);

        // 正常情况下这里一定是 200 + HTML；其它状态（重定向、404、异常页）原样透传，
        // 绝不改 header、绝不写 body，免得把重定向/错误响应弄坏。
        if (context.Response.StatusCode != StatusCodes.Status200OK)
        {
            // 304/重定向/错误页原样透传。注意：304 的 body 就是空的，往 304 响应里写字节
            // 会被 Kestrel 直接抛 InvalidOperationException（旧版就是这样，每个带缓存的浏览器
            // 每次加载都在日志里刷一条 ERR），所以这里必须判长度。
            if (buffer.Length > 0)
            {
                await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            }

            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            if (!html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            {
                // 版本戳：每次 Jellyfin 重启都会换 URL，浏览器不可能拿到旧脚本
                var version = _startedAt;
                var block = new StringBuilder();

                // 样式由 smooth.js 自己注入并守护（web 客户端启动时会清掉 <style>），
                // 这里只负责脚本。
                foreach (var script in Scripts)
                {
                    block.Append("<script defer src=\"../FeatureEnhance/Client/")
                        .Append(script)
                        .Append("?v=")
                        .Append(version)
                        .Append("\"></script>");
                }

                var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyClose >= 0)
                {
                    html = html[..bodyClose] + block + "\n" + html[bodyClose..];
                }

                // 头部引导脚本：必须在 app 的 bundle 之前同步执行 —— jellyfin-apiclient 在
                // 模块初始化时就把 window.fetch 的引用抓走了，晚注入的包装根本不会生效。
                // 它只做一件事：把"搜索页那些 searchTerm 请求"接过来直接回空结果（不打服务器），
                // 因为搜索结果完全由本插件自己的接口提供（四个分类方格 + 原生列表页桥接）。
                var headClose = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                if (headClose >= 0)
                {
                    var headEnd = headClose + "<head>".Length;
                    html = html[..headEnd] + Bootstrap + html[headEnd..];
                }

                if (System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                {
                    _logger.LogInformation(
                        "Feature Enhance: injected {Count} client scripts into index.html via request-time middleware.",
                        Scripts.Length);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Feature Enhance: script injection failed, serving original HTML.");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // 这两个 Remove 其实是无效的（静态文件中间件在响应开始时才写它们，晚于这里），
        // 真正起作用的是上面把 If-None-Match/If-Modified-Since 请求头去掉；留着只是表明意图。
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }
}
