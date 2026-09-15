using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Search API used by the injected "folders in search results" section. Unlike the built-in
/// search it does not drop folders, and it falls back to fuzzy folder name matching.
///
/// 权限口径与原生完全一致：用户身份只取自认证票据（不再相信查询参数里的 userId），
/// 并且用 Jellyfin 自己的 <see cref="IItemQueryHelpers.ApplyAccessFiltering"/> 做库访问 +
/// 分级 + 标签过滤 —— 之前只按 TopParentIds 过滤，会把受限条目的名称/路径/数量漏出去。
///
/// 列表页的原生筛选/排序也走 Jellyfin 自己的 <c>TranslateQuery</c> / <c>ApplyOrder</c>，
/// 不再只支持 4 个筛选器。
/// </summary>
[ApiController]
[Authorize]
[Route("FeatureEnhance")]
public class SearchApiController : ControllerBase
{
    private const int MaxLimit = 500;
    private const int MaxFoldersScanned = 20000;

    /// <summary>模糊匹配时最多扫多少行候选（先用 SQL 粗筛，再在内存里打分）。</summary>
    private const int MaxFuzzyCandidates = 6000;

    /// <summary>模糊匹配最多返回多少条（计数与分页都用这个上限，保证两边一致）。</summary>
    private const int MaxFuzzyResults = 300;

    /// <summary>模糊匹配的最低分（ScoreFuzzy：近似子串 44 / 整体近似 46 / 包含 52 …）。</summary>
    private const float FuzzyThreshold = 44f;

    /// <summary>
    /// 播放/随机播放时最多下钻多少个匹配到的文件夹。
    /// 实测（14.6 万条目）：随机排序必须先把命中的条目全部算出来才能排序，文件夹越多越慢 ——
    /// 20 个 ≈ 0.36s、60 个 ≈ 0.65s、200 个 ≈ 1.6s；不排序的话 0.1s 就够（可提前终止）。
    /// 随机只需要 300 条，60 个文件夹的并集绰绰有余，所以卡在 60。
    /// </summary>
    private const int MaxPlayableFolders = 60;

    /// <summary>播放/随机播放一次最多取多少条（原生列表页 shuffle 用的是 300）。</summary>
    private const int MaxPlayableItems = 500;

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemQueryHelpers _queryHelpers;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchApiController"/> class.
    /// </summary>
    /// <param name="dbProvider">Database context factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="queryHelpers">Jellyfin's own query builder (access filter, native filters, ordering).</param>
    public SearchApiController(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IItemQueryHelpers queryHelpers)
    {
        _dbProvider = dbProvider;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queryHelpers = queryHelpers;
    }

    /// <summary>
    /// Searches videos, photos, folders or arbitrary item types, with real paging.
    /// </summary>
    /// <param name="term">Search term.</param>
    /// <param name="kind">video | photo | folder | item.</param>
    /// <param name="itemTypes">kind=item：逗号分隔的客户端类型名（Movie,Series,Episode,Video,Photo,PhotoAlbum,…）。</param>
    /// <param name="match">name | path | auto（默认 auto：先按名称匹配，名称没有结果才退到路径匹配）。</param>
    /// <param name="filters">原生列表页的筛选：IsPlayed,IsUnplayed,IsFavorite,IsResumable（逗号分隔）。</param>
    /// <param name="startIndex">First record.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sortBy">原生排序键（SortName / DateCreated / DatePlayed / Random / CommunityRating …）。</param>
    /// <param name="sortOrder">Ascending | Descending.</param>
    /// <param name="videoTypes">原生筛选：VideoFile,Iso,BluRay,Dvd,…</param>
    /// <param name="isHd">原生筛选：高清。</param>
    /// <param name="is4K">原生筛选：4K。</param>
    /// <param name="is3D">原生筛选：3D。</param>
    /// <param name="hasSubtitles">原生筛选：有字幕。</param>
    /// <param name="hasTrailer">原生筛选：有预告片。</param>
    /// <param name="hasThemeSong">原生筛选：有主题曲。</param>
    /// <param name="hasThemeVideo">原生筛选：有主题视频。</param>
    /// <param name="hasSpecialFeature">原生筛选：有花絮。</param>
    /// <param name="tags">原生筛选：标签（逗号分隔）。</param>
    /// <param name="genres">原生筛选：类型（逗号分隔）。</param>
    /// <param name="nameStartsWith">原生字母选择器：首字母。</param>
    /// <param name="nameLessThan">原生字母选择器：#（非字母开头）。</param>
    /// <param name="userId">已废弃：忽略，身份一律取自认证票据。</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged result.</returns>
    [HttpGet("Search")]
    public async Task<ActionResult<SearchPageResult>> Search(
        [FromQuery] string? term,
        [FromQuery] string? kind,
        [FromQuery] string? itemTypes,
        [FromQuery] string? match,
        [FromQuery] string? filters,
        [FromQuery] int startIndex,
        [FromQuery] int limit,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortOrder,
        [FromQuery] string? videoTypes,
        [FromQuery] string? isHd,
        [FromQuery] string? is4K,
        [FromQuery] string? is3D,
        [FromQuery] string? hasSubtitles,
        [FromQuery] string? hasTrailer,
        [FromQuery] string? hasThemeSong,
        [FromQuery] string? hasThemeVideo,
        [FromQuery] string? hasSpecialFeature,
        [FromQuery] string? tags,
        [FromQuery] string? genres,
        [FromQuery] string? nameStartsWith,
        [FromQuery] string? nameLessThan,
        [FromQuery] string? userId,
        CancellationToken cancellationToken)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? "folder" : kind.Trim().ToLowerInvariant();
        term = (term ?? string.Empty).Trim();

        var result = new SearchPageResult
        {
            Term = term,
            Kind = kind,
            StartIndex = Math.Max(0, startIndex),
            Limit = Math.Clamp(limit <= 0 ? 60 : limit, 1, MaxLimit)
        };

        var clean = PathMatcher.Normalize(term);
        if (term.Length < 1 || clean.Length == 0)
        {
            // 单个汉字（如"杂"）也要能搜出文件夹
            return result;
        }

        var user = ResolveUser();
        var accessFilter = BuildAccessFilter(user);
        var nativeFilter = BuildNativeFilter(user, sortBy, sortOrder, videoTypes, isHd, is4K, is3D, hasSubtitles, hasTrailer, hasThemeSong, hasThemeVideo, hasSpecialFeature, tags, genres, nameStartsWith, nameLessThan, filters);

        var like = "%" + PathMatcher.EscapeLike(term) + "%";

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.BaseItems.AsNoTracking().Where(e => !e.IsVirtualItem);

        var types = ParseItemTypes(itemTypes);

        switch (kind)
        {
            case "photo":
                query = query.Where(e => e.MediaType == "Photo");
                break;
            case "folder":
                query = query.Where(e => e.IsFolder);
                break;
            case "item":
                if (types.Length == 0)
                {
                    return result;
                }

                // 只按类型过滤：PhotoAlbum / Series / BoxSet 这些本身 IsFolder=1，
                // 再叠一个 !IsFolder 会把它们整个排除掉（踩过：相册分区永远是 0）
                query = query.Where(ItemTypeFilter(types));
                break;
            default:
                query = query.Where(e => !e.IsFolder && e.MediaType == "Video");
                break;
        }

        // 名称匹配 / 路径匹配：
        //   搜索页用的是"按名称搜"（和原生搜索框一致），只有当这个类型一条名称都匹配不上时，
        //   才退回到插件特有的"路径匹配"（输入文件夹名 → 找到里面的视频）。
        //   两种口径的条数都用 COUNT 精确统计，翻页才可能对得上。
        var nameMatch = (Expression<Func<BaseItemEntity, bool>>)(e =>
            (e.CleanName != null && e.CleanName.Contains(clean))
            || (e.Name != null && e.Name.Contains(term)));
        var pathMatch = (Expression<Func<BaseItemEntity, bool>>)(e =>
            EF.Functions.Like(e.Path!, like, "\\")
            || (e.CleanName != null && e.CleanName.Contains(clean)));

        // 权限过滤：与原生同一个实现（库访问 + 分级 + 标签），Search 和 Counts 都用它，
        // 两边口径才不会分叉。
        if (accessFilter is not null)
        {
            query = _queryHelpers.ApplyAccessFiltering(db, query, accessFilter);
        }

        // 原生列表页带来的筛选条件，交给 Jellyfin 自己的翻译器（含 4K/HD/字幕/预告/标签/类型…），
        // 这样"点进去以后按 4K 筛选"才真的会生效，而不是界面选中了但结果不变。
        query = _queryHelpers.TranslateQuery(query, db, nativeFilter);

        var matchMode = (match ?? "auto").Trim().ToLowerInvariant();
        if (matchMode == "path")
        {
            query = query.Where(pathMatch);
            result.Match = "path";
        }
        else if (matchMode == "name")
        {
            query = query.Where(nameMatch);
            result.Match = "name";
        }
        else if (matchMode == "fuzzy")
        {
            var fuzzyItems = await FindFuzzyAsync(db, clean, types, kind == "folder", accessFilter, nativeFilter, cancellationToken).ConfigureAwait(false);

            result.Match = "fuzzy";
            result.Total = fuzzyItems.Count;
            result.Items = fuzzyItems
                .Skip(result.StartIndex)
                .Take(result.Limit)
                .ToList();
            return result;
        }
        else
        {
            var nameProbe = query.Where(nameMatch);
            if (await nameProbe.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                query = nameProbe;
                result.Match = "name";
            }
            else
            {
                var pathProbe = query.Where(pathMatch);
                if (await pathProbe.AnyAsync(cancellationToken).ConfigureAwait(false))
                {
                    query = pathProbe;
                    result.Match = "path";
                }
                else
                {
                    // 名称、路径都没有 → 模糊匹配（编辑距离/子序列）。
                    // 用"打分成"的确定列表同时给出 Total 和这一页，所以计数、翻页永远一致。
                    var fuzzyItems = await FindFuzzyAsync(db, clean, types, kind == "folder", accessFilter, nativeFilter, cancellationToken).ConfigureAwait(false);

                    result.Match = "fuzzy";
                    result.Total = fuzzyItems.Count;
                    result.Items = fuzzyItems
                        .Skip(result.StartIndex)
                        .Take(result.Limit)
                        .ToList();
                    return result;
                }
            }
        }

        result.Total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        // 排序也用原生的（DatePlayed 走 UserData、随机、评分、时长… 都和列表页的排序菜单一致）
        query = _queryHelpers.ApplyOrder(query, nativeFilter, db);

        var rows = await query
            .Skip(result.StartIndex)
            .Take(result.Limit)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Type,
                e.Path,
                e.RunTimeTicks,
                HasImage = db.BaseItemImageInfos.Any(i => i.ItemId == e.Id)
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(r => ToItem(r.Id, r.Name, r.Type, r.Path, r.RunTimeTicks, r.HasImage))
            .ToList();

        // 文件夹本身通常没有图片：用"文件夹内第一个有图的子项"作为它的缩略图
        if (kind == "folder" && items.Count > 0)
        {
            var withImage = await AttachFolderThumbnailsAsync(db, items, cancellationToken).ConfigureAwait(false);
            if (withImage.Count > 0)
            {
                items = withImage;
            }
        }

        result.Items = items;
        return result;
    }

    /// <summary>
    /// 每个"分组"各有多少条命中。分组由调用方给出（搜索页的四个方格就是四组）：
    /// 每组是一串客户端类型名，或者 <c>@folders</c> 表示所有文件夹。
    /// 口径与 <see cref="Search"/> **完全一致**：名称 → 路径 → 模糊，逐级下降，
    /// 并且把最终用的是哪一级回传（match），列表页按同一级去取，所以"方格上的数字"永远等于"列表里的条数"。
    /// </summary>
    /// <param name="term">Search term.</param>
    /// <param name="groups">分组，分号分隔；每组逗号分隔类型名，或 @folders。</param>
    /// <param name="match">强制口径：name | path | fuzzy（默认 auto）。</param>
    /// <param name="userId">已废弃：忽略，身份一律取自认证票据。</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per group counts.</returns>
    [HttpGet("Search/Counts")]
    public async Task<ActionResult<SearchCountsResult>> Counts(
        [FromQuery] string? term,
        [FromQuery] string? groups,
        [FromQuery] string? match,
        [FromQuery] string? userId,
        CancellationToken cancellationToken)
    {
        term = (term ?? string.Empty).Trim();
        var clean = PathMatcher.Normalize(term);
        var result = new SearchCountsResult { Term = term };
        if (term.Length < 1 || clean.Length == 0)
        {
            return result;
        }

        var requested = ParseGroups(groups);
        if (requested.Count == 0)
        {
            return result;
        }

        var user = ResolveUser();
        var accessFilter = BuildAccessFilter(user);

        var like = "%" + PathMatcher.EscapeLike(term) + "%";
        var nameMatch = (Expression<Func<BaseItemEntity, bool>>)(e =>
            (e.CleanName != null && e.CleanName.Contains(clean))
            || (e.Name != null && e.Name.Contains(term)));
        var pathMatch = (Expression<Func<BaseItemEntity, bool>>)(e =>
            EF.Functions.Like(e.Path!, like, "\\")
            || (e.CleanName != null && e.CleanName.Contains(clean)));
        var matchMode = (match ?? "auto").Trim().ToLowerInvariant();

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        foreach (var group in requested)
        {
            var baseQuery = db.BaseItems.AsNoTracking().Where(e => !e.IsVirtualItem);

            baseQuery = group.FoldersOnly
                ? baseQuery.Where(e => e.IsFolder)
                : baseQuery.Where(ItemTypeFilter(group.Types));

            if (accessFilter is not null)
            {
                baseQuery = _queryHelpers.ApplyAccessFiltering(db, baseQuery, accessFilter);
            }

            int total;
            string mode;

            if (matchMode == "fuzzy")
            {
                var fuzzyItems = await FindFuzzyAsync(db, clean, group.Types, group.FoldersOnly, accessFilter, null, cancellationToken).ConfigureAwait(false);
                total = fuzzyItems.Count;
                mode = "fuzzy";
            }
            else if (matchMode == "path")
            {
                total = await baseQuery.Where(pathMatch).CountAsync(cancellationToken).ConfigureAwait(false);
                mode = "path";
            }
            else if (matchMode == "name")
            {
                total = await baseQuery.Where(nameMatch).CountAsync(cancellationToken).ConfigureAwait(false);
                mode = "name";
            }
            else
            {
                total = await baseQuery.Where(nameMatch).CountAsync(cancellationToken).ConfigureAwait(false);
                mode = "name";

                if (total == 0)
                {
                    total = await baseQuery.Where(pathMatch).CountAsync(cancellationToken).ConfigureAwait(false);
                    mode = "path";
                }

                if (total == 0)
                {
                    var fuzzyItems = await FindFuzzyAsync(db, clean, group.Types, group.FoldersOnly, accessFilter, null, cancellationToken).ConfigureAwait(false);
                    total = fuzzyItems.Count;
                    mode = "fuzzy";
                }
            }

            result.Groups.Add(new SearchCountsGroup
            {
                Types = group.FoldersOnly ? "@folders" : string.Join(',', group.Types),
                Total = total,
                Match = mode
            });

            result.Total += total;
        }

        return result;
    }

    /// <summary>
    /// 当前登录用户；API key（票据里没有用户 id）返回 null，按原生规则视为全库可访问。
    /// </summary>
    private User? ResolveUser()
    {
        var userId = PluginUser.GetUserId(User);
        if (userId == Guid.Empty)
        {
            return null;
        }

        return _userManager.GetUserById(userId);
    }

    /// <summary>
    /// 原生口径的访问过滤器（库访问 + 分级 + 标签）。用法与
    /// <c>Emby.Server.Implementations.Library.Search.SearchQueryAccessFilter</c> 一致。
    /// </summary>
    private InternalItemsQuery? BuildAccessFilter(User? user)
    {
        if (user is null)
        {
            return null;
        }

        var accessFilter = new InternalItemsQuery(user)
        {
            IncludeItemsByName = true
        };

        _libraryManager.ConfigureUserAccess(accessFilter, user);
        return accessFilter;
    }

    /// <summary>
    /// 把列表页带来的原生筛选/排序参数装成 InternalItemsQuery，交给 Jellyfin 自己的
    /// TranslateQuery / ApplyOrder 翻译 —— 手写 SQL 不可能覆盖 4K/HD/字幕/预告/标签这些维度。
    /// </summary>
    private static InternalItemsQuery BuildNativeFilter(
        User? user,
        string? sortBy,
        string? sortOrder,
        string? videoTypes,
        string? isHd,
        string? is4K,
        string? is3D,
        string? hasSubtitles,
        string? hasTrailer,
        string? hasThemeSong,
        string? hasThemeVideo,
        string? hasSpecialFeature,
        string? tags,
        string? genres,
        string? nameStartsWith,
        string? nameLessThan,
        string? filters)
    {
        var filter = new InternalItemsQuery(user)
        {
            IsHD = ParseBool(isHd),
            Is4K = ParseBool(is4K),
            Is3D = ParseBool(is3D),
            HasSubtitles = ParseBool(hasSubtitles),
            HasTrailer = ParseBool(hasTrailer),
            HasThemeSong = ParseBool(hasThemeSong),
            HasThemeVideo = ParseBool(hasThemeVideo),
            HasSpecialFeature = ParseBool(hasSpecialFeature),
            Tags = ParseList(tags).ToArray(),
            Genres = ParseList(genres),
            NameStartsWith = string.IsNullOrWhiteSpace(nameStartsWith) ? null : nameStartsWith.Trim(),
            NameLessThan = string.IsNullOrWhiteSpace(nameLessThan) ? null : nameLessThan.Trim(),
            VideoTypes = ParseVideoTypes(videoTypes),
            OrderBy = ParseOrderBy(sortBy, sortOrder)
        };

        // 播放状态 / 收藏 / 继续观看：同样交给原生翻译器（原生对"文件夹算不算已看"有自己的口径）
        if (!string.IsNullOrWhiteSpace(filters))
        {
            var wanted = filters
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (wanted.Contains("IsPlayed"))
            {
                filter.IsPlayed = true;
            }

            if (wanted.Contains("IsUnplayed"))
            {
                filter.IsPlayed = false;
            }

            if (wanted.Contains("IsFavorite"))
            {
                filter.IsFavorite = true;
            }

            if (wanted.Contains("IsResumable"))
            {
                filter.IsResumable = true;
            }
        }

        return filter;
    }

    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => null
        };
    }

    private static IReadOnlyList<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static VideoType[] ParseVideoTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var types = new List<VideoType>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<VideoType>(part, true, out var parsed))
            {
                types.Add(parsed);
            }
        }

        return types.ToArray();
    }

    /// <summary>
    /// 原生排序键 → ItemSortBy。旧的 name/added/played/random 简写仍然兼容。
    /// </summary>
    private static IReadOnlyList<(ItemSortBy OrderBy, SortOrder SortOrder)> ParseOrderBy(string? sortBy, string? sortOrder)
    {
        var raw = (sortBy ?? string.Empty).Split(',')[0].Trim();
        if (raw.Length == 0)
        {
            return [];
        }

        var key = raw.ToLowerInvariant() switch
        {
            "name" => ItemSortBy.SortName,
            "added" => ItemSortBy.DateCreated,
            "played" => ItemSortBy.DatePlayed,
            "random" => ItemSortBy.Random,
            _ => Enum.TryParse<ItemSortBy>(raw, true, out var parsed) ? parsed : ItemSortBy.SortName
        };

        var descending = (sortOrder ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "desc" or "descending" => true,
            _ => false
        };

        return [(key, descending ? SortOrder.Descending : SortOrder.Ascending)];
    }

    /// <summary>
    /// "文件夹"格子的列表页里，原生播放/随机播放/加入队列拿到的是一条条**文件夹**，
    /// 播放器放不了（就是"点随机播放没反应"的原因）。这里下钻一层：
    /// 用与 <see cref="Search"/> kind=folder **完全相同的匹配口径**（名称 → 路径 → 模糊）
    /// 找出匹配的文件夹，再取它们内部的可播放条目（非文件夹、非虚拟、有媒体类型的叶子），
    /// 交给原生播放器去播。权限、原生筛选、排序都跟列表页保持一致。
    /// </summary>
    /// <param name="term">Search term.</param>
    /// <param name="match">name | path | fuzzy | auto（默认 auto）。</param>
    /// <param name="shuffle">1 = 随机顺序（对应原生的 Shuffle 按钮）。</param>
    /// <param name="limit">最多返回多少条.</param>
    /// <param name="sortBy">非随机时的排序键（与列表页一致）。</param>
    /// <param name="sortOrder">Ascending | Descending.</param>
    /// <param name="filters">已看/未看/收藏/继续观看.</param>
    /// <param name="videoTypes">原生筛选：视频类型.</param>
    /// <param name="isHd">原生筛选：高清.</param>
    /// <param name="is4K">原生筛选：4K.</param>
    /// <param name="is3D">原生筛选：3D.</param>
    /// <param name="hasSubtitles">原生筛选：有字幕.</param>
    /// <param name="hasTrailer">原生筛选：有预告片.</param>
    /// <param name="hasThemeSong">原生筛选：有主题曲.</param>
    /// <param name="hasThemeVideo">原生筛选：有主题视频.</param>
    /// <param name="hasSpecialFeature">原生筛选：有花絮.</param>
    /// <param name="tags">原生筛选：标签.</param>
    /// <param name="genres">原生筛选：类型.</param>
    /// <param name="userId">已废弃：忽略.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>可播放条目（按随机或指定顺序）.</returns>
    [HttpGet("Search/Playable")]
    public async Task<ActionResult<SearchPageResult>> Playable(
        [FromQuery] string? term,
        [FromQuery] string? match,
        [FromQuery] string? shuffle,
        [FromQuery] int limit,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortOrder,
        [FromQuery] string? filters,
        [FromQuery] string? videoTypes,
        [FromQuery] string? isHd,
        [FromQuery] string? is4K,
        [FromQuery] string? is3D,
        [FromQuery] string? hasSubtitles,
        [FromQuery] string? hasTrailer,
        [FromQuery] string? hasThemeSong,
        [FromQuery] string? hasThemeVideo,
        [FromQuery] string? hasSpecialFeature,
        [FromQuery] string? tags,
        [FromQuery] string? genres,
        [FromQuery] string? userId,
        CancellationToken cancellationToken)
    {
        term = (term ?? string.Empty).Trim();
        var clean = PathMatcher.Normalize(term);
        var result = new SearchPageResult
        {
            Term = term,
            Kind = "playable",
            StartIndex = 0,
            Limit = Math.Clamp(limit <= 0 ? 300 : limit, 1, MaxPlayableItems)
        };

        if (term.Length < 1 || clean.Length == 0)
        {
            return result;
        }

        var user = ResolveUser();
        var accessFilter = BuildAccessFilter(user);
        var nativeFilter = BuildNativeFilter(user, sortBy, sortOrder, videoTypes, isHd, is4K, is3D, hasSubtitles, hasTrailer, hasThemeSong, hasThemeVideo, hasSpecialFeature, tags, genres, null, null, filters);
        var isShuffle = ParseBool(shuffle) ?? false;

        await using var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var folderIds = await FindMatchingFolderIdsAsync(db, term, clean, match, accessFilter, cancellationToken).ConfigureAwait(false);
        if (folderIds.Count == 0)
        {
            return result;
        }

        // 文件夹**内部**的可播放条目（AncestorIds 是完整祖先链，所以这是任意层级的后代）
        var itemsQuery = db.BaseItems
            .AsNoTracking()
            .Where(e => !e.IsVirtualItem
                && !e.IsFolder
                && e.MediaType != null
                && e.Parents!.Any(p => folderIds.Contains(p.ParentItemId)));

        if (accessFilter is not null)
        {
            itemsQuery = _queryHelpers.ApplyAccessFiltering(db, itemsQuery, accessFilter);
        }

        itemsQuery = _queryHelpers.TranslateQuery(itemsQuery, db, nativeFilter);

        itemsQuery = isShuffle
            ? itemsQuery.OrderBy(e => EF.Functions.Random())
            : _queryHelpers.ApplyOrder(itemsQuery, nativeFilter, db);

        var rows = await itemsQuery
            .Take(result.Limit)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Type,
                e.Path,
                e.RunTimeTicks,
                HasImage = db.BaseItemImageInfos.Any(i => i.ItemId == e.Id)
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        result.Items = rows
            .Select(r => ToItem(r.Id, r.Name, r.Type, r.Path, r.RunTimeTicks, r.HasImage))
            .ToList();
        result.Total = result.Items.Count;
        return result;
    }

    /// <summary>
    /// 找出与搜索词匹配的文件夹 id（口径与 <see cref="Search"/> kind=folder 一致：
    /// name → path → fuzzy 逐级下降，auto 时用第一个有命中的层级）。
    /// </summary>
    private async Task<List<Guid>> FindMatchingFolderIdsAsync(
        JellyfinDbContext db,
        string term,
        string clean,
        string? match,
        InternalItemsQuery? accessFilter,
        CancellationToken cancellationToken)
    {
        var like = "%" + PathMatcher.EscapeLike(term) + "%";
        var nameMatch = (Expression<Func<BaseItemEntity, bool>>)(e =>
            (e.CleanName != null && e.CleanName.Contains(clean))
            || (e.Name != null && e.Name.Contains(term)));
        var pathMatch = (Expression<Func<BaseItemEntity, bool>>)(e =>
            EF.Functions.Like(e.Path!, like, "\\")
            || (e.CleanName != null && e.CleanName.Contains(clean)));

        var query = db.BaseItems.AsNoTracking().Where(e => !e.IsVirtualItem && e.IsFolder);
        if (accessFilter is not null)
        {
            query = _queryHelpers.ApplyAccessFiltering(db, query, accessFilter);
        }

        query = query.OrderBy(e => e.Id);

        Task<List<Guid>> Take(IQueryable<BaseItemEntity> q) =>
            q.Take(MaxPlayableFolders).Select(e => e.Id).ToListAsync(cancellationToken);

        // 模糊：在内存里按最后的路径分片打分（与 FindFuzzyFoldersAsync 同一套阈值）
        async Task<List<Guid>> FuzzyAsync()
        {
            var folders = await query
                .Take(MaxFoldersScanned)
                .Select(e => new { e.Id, e.Path })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var ids = new List<Guid>();
            foreach (var folder in folders)
            {
                var segments = PathMatcher.Segments(folder.Path!);
                if (segments.Length > 0 && PathMatcher.ScoreSegment(segments[^1], clean) >= 34f)
                {
                    ids.Add(folder.Id);
                    if (ids.Count >= MaxPlayableFolders)
                    {
                        break;
                    }
                }
            }

            return ids;
        }

        var mode = (match ?? "auto").Trim().ToLowerInvariant();
        if (mode == "name")
        {
            return await Take(query.Where(nameMatch)).ConfigureAwait(false);
        }

        if (mode == "path")
        {
            return await Take(query.Where(pathMatch)).ConfigureAwait(false);
        }

        if (mode == "fuzzy")
        {
            return await FuzzyAsync().ConfigureAwait(false);
        }

        var byName = await Take(query.Where(nameMatch)).ConfigureAwait(false);
        if (byName.Count > 0)
        {
            return byName;
        }

        var byPath = await Take(query.Where(pathMatch)).ConfigureAwait(false);
        if (byPath.Count > 0)
        {
            return byPath;
        }

        return await FuzzyAsync().ConfigureAwait(false);
    }

    private sealed record SearchGroup(string[] Types, bool FoldersOnly);

    private static List<SearchGroup> ParseGroups(string? groups)
    {
        var list = new List<SearchGroup>();
        if (string.IsNullOrWhiteSpace(groups))
        {
            return list;
        }

        foreach (var raw in groups.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('@'))
            {
                list.Add(new SearchGroup([], true));
                continue;
            }

            var types = ParseItemTypes(raw);
            if (types.Length > 0)
            {
                list.Add(new SearchGroup(types, false));
            }
        }

        return list;
    }

    /// <summary>
    /// 模糊匹配：先按"名称里至少含词里的一个字符"用 SQL 粗筛（把扫描量卡在 MaxFuzzyCandidates 行），
    /// 再用 <see cref="PathMatcher.ScoreFuzzy"/> 打分排序（分数降序、同分按名字），取前 MaxFuzzyResults 条。
    /// </summary>
    /// <remarks>
    /// 关键点：排序**完全确定**（不依赖数据库顺序），所以同一个词每次调用得到的是同一份列表 ——
    /// 计数接口和分页接口拿到的是同一个集合，翻页也就对得上了。
    /// </remarks>
    private async Task<List<SearchPageItem>> FindFuzzyAsync(
        JellyfinDbContext db,
        string clean,
        string[] types,
        bool foldersOnly,
        InternalItemsQuery? accessFilter,
        InternalItemsQuery? nativeFilter,
        CancellationToken cancellationToken)
    {
        var query = db.BaseItems.AsNoTracking().Where(e => !e.IsVirtualItem);

        query = foldersOnly
            ? query.Where(e => e.IsFolder)
            : query.Where(ItemTypeFilter(types));

        if (accessFilter is not null)
        {
            query = _queryHelpers.ApplyAccessFiltering(db, query, accessFilter);
        }

        if (nativeFilter is not null)
        {
            query = _queryHelpers.TranslateQuery(query, db, nativeFilter);
        }

        // SQL 粗筛：名称里至少含一个查询字符（"网凰"→ 含"网"或"凰"的行都算候选）
        var chars = clean
            .Where(c => c != ' ')
            .Distinct()
            .Take(4)
            .Select(c => c.ToString())
            .ToArray();

        if (chars.Length > 0)
        {
            query = query.Where(ContainsAnyFilter(chars));
        }

        // OrderBy(Id) 是必须的：粗筛取前 6000 行，"计数"和"翻页"必须是同一批候选，
        // 而 SQLite 不加 ORDER BY 的行序只是"碰巧稳定"（EF 也会为此刷 warning）。
        // 实测加排序后耗时不变（24ms vs 25ms，rowid 顺序直接满足 LIMIT）。
        var rows = await query
            .OrderBy(e => e.Id)
            .Take(MaxFuzzyCandidates)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Type,
                e.Path,
                e.RunTimeTicks,
                HasImage = db.BaseItemImageInfos.Any(i => i.ItemId == e.Id)
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var scored = new List<(SearchPageItem Item, float Score)>();
        foreach (var row in rows)
        {
            var score = PathMatcher.ScoreFuzzy(row.Name, clean);
            if (score < FuzzyThreshold)
            {
                continue;
            }

            scored.Add((ToItem(row.Id, row.Name, row.Type, row.Path, row.RunTimeTicks, row.HasImage), score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFuzzyResults)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// "名称里含 chars 里的任意一个字符" 的 OR 表达式（EF 能翻成 OR 起来的 LIKE）。
    /// </summary>
    private static Expression<Func<BaseItemEntity, bool>> ContainsAnyFilter(string[] chars)
    {
        var parameter = Expression.Parameter(typeof(BaseItemEntity), "e");
        var nameProperty = Expression.Property(parameter, nameof(BaseItemEntity.CleanName));
        var notNull = Expression.NotEqual(nameProperty, Expression.Constant(null, typeof(string)));
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

        Expression? body = null;
        foreach (var ch in chars)
        {
            var clause = Expression.AndAlso(
                notNull,
                Expression.Call(nameProperty, contains, Expression.Constant(ch, typeof(string))));
            body = body is null ? clause : Expression.OrElse(body, clause);
        }

        return Expression.Lambda<Func<BaseItemEntity, bool>>(body!, parameter);
    }

    /// <summary>
    /// 把 "Movie,Series,Episode" 解析成类型名数组。
    /// </summary>
    private static string[] ParseItemTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0 && t.All(c => char.IsLetterOrDigit(c) || c == '_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 数据库里存的是完整类型名（MediaBrowser.Controller.Entities.Movies.Movie），
    /// 这里按".类型名"结尾过滤，等价于客户端的 Type。
    /// </summary>
    private static Expression<Func<BaseItemEntity, bool>> ItemTypeFilter(string[] types)
    {
        var parameter = Expression.Parameter(typeof(BaseItemEntity), "e");
        var typeProperty = Expression.Property(parameter, nameof(BaseItemEntity.Type));
        var notNull = Expression.NotEqual(typeProperty, Expression.Constant(null, typeof(string)));
        var endsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

        Expression? body = null;
        foreach (var type in types)
        {
            var clause = Expression.AndAlso(
                notNull,
                Expression.Call(typeProperty, endsWith, Expression.Constant("." + type, typeof(string))));
            body = body is null ? clause : Expression.OrElse(body, clause);
        }

        return Expression.Lambda<Func<BaseItemEntity, bool>>(body!, parameter);
    }

    /// <summary>
    /// For folder results, find one child item that has a primary image so the card can show a
    /// real thumbnail instead of a bare folder icon. One query for the whole page.
    /// </summary>
    private static async Task<List<SearchPageItem>> AttachFolderThumbnailsAsync(
        JellyfinDbContext db,
        List<SearchPageItem> items,
        CancellationToken cancellationToken)
    {
        var prefixes = items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .Select(i => PathMatcher.EscapeLike(i.Path.TrimEnd('/')) + "/%")
            .ToArray();

        if (prefixes.Length == 0)
        {
            return [];
        }

        var candidates = await db.BaseItems
            .AsNoTracking()
            .Where(e => !e.IsFolder && !e.IsVirtualItem && e.Path != null)
            .Where(e => db.BaseItemImageInfos.Any(i => i.ItemId == e.Id))
            .Where(e => prefixes.Any(p => EF.Functions.Like(e.Path!, p, "\\")))
            .OrderBy(e => e.Id)
            .Select(e => new { e.Id, e.Path })
            .Take(1000)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        var result = new List<SearchPageItem>(items.Count);
        foreach (var item in items)
        {
            var prefix = item.Path.TrimEnd('/') + "/";
            var match = candidates.FirstOrDefault(c =>
                c.Path is not null && c.Path.StartsWith(prefix, StringComparison.Ordinal));
            result.Add(match is null ? item : item with { ImageItemId = match.Id, HasImage = true });
        }

        return result;
    }

    private static SearchPageItem ToItem(
        Guid id,
        string? name,
        string? type,
        string? path,
        long? runTimeTicks,
        bool hasImage) => new(
        id,
        name ?? string.Empty,
        (type ?? string.Empty).Split('.').Last(),
        path ?? string.Empty,
        DirectoryOf(path ?? string.Empty),
        runTimeTicks,
        hasImage);

    private static string DirectoryOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index > 0 ? path[..index] : string.Empty;
    }
}

/// <summary>
/// Paged search result.
/// </summary>
public sealed class SearchPageResult
{
    /// <summary>Gets or sets the search term.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the total number of exact matches.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the first record index.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    public int Limit { get; set; }

    /// <summary>Gets or sets the matching mode actually used: name | path | fuzzy.</summary>
    public string Match { get; set; } = "name";

    /// <summary>Gets or sets the items.</summary>
    public IReadOnlyList<SearchPageItem> Items { get; set; } = [];
}

/// <summary>
/// 每个分组各有多少条命中。
/// </summary>
public sealed class SearchCountsResult
{
    /// <summary>Gets or sets the search term.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Gets or sets the total number of matches（四组之和）.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the per group counts（方格用：每组一个数字 + 一个口径）。</summary>
    public List<SearchCountsGroup> Groups { get; set; } = [];
}

/// <summary>
/// 一个分组的命中数。
/// </summary>
public sealed class SearchCountsGroup
{
    /// <summary>Gets or sets the group's type names（@folders 表示所有文件夹）。</summary>
    public string Types { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of matches.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the matching mode used: name | path | fuzzy.</summary>
    public string Match { get; set; } = "name";
}

/// <summary>
/// One search result row.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Item type name.</param>
/// <param name="Path">Full path.</param>
/// <param name="Folder">Containing folder.</param>
/// <param name="RunTimeTicks">Runtime in ticks.</param>
/// <param name="HasImage">Whether the item has a primary image.</param>
/// <param name="ImageItemId">Item id whose image should be shown (folder thumbnails).</param>
public sealed record SearchPageItem(
    Guid Id,
    string Name,
    string Type,
    string Path,
    string Folder,
    long? RunTimeTicks,
    bool HasImage,
    Guid? ImageItemId = null);
