using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Search provider that matches the file path / folder names in addition to item names,
/// with fuzzy (typo tolerant) fallback for folder names. Results are spread across every
/// matching folder instead of filling the page from a single one.
///
/// 作用域与原生一致：调用方要的类型（IncludeItemTypes/ExcludeItemTypes）和父级（ParentId）
/// 都会套上 —— 之前忽略它们，插件塞进来的域外候选会挤掉原生本该返回的结果
/// （下游是在合并并截断 limit 之后才按类型/父级过滤的）。
/// 访问过滤也改成原生实现（库访问 + 分级 + 标签），不再只按 TopParentIds。
/// </summary>
public sealed class PathSearchProvider : IInternalSearchProvider
{
    private const int MaxCandidates = 20000;
    private const int MaxFuzzyFolders = 12;
    private const int FallbackItemsPerFolder = 60;
    private const int MaxFoldersScanned = 20000;
    private const float FolderFuzzyPenalty = 6f;
    private const float DiversityPenaltyStep = 0.25f;
    private const int DiversityMaxSteps = 60;

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IItemQueryHelpers _queryHelpers;
    private readonly IItemTypeLookup _itemTypeLookup;
    private readonly ILogger<PathSearchProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PathSearchProvider"/> class.
    /// </summary>
    /// <param name="dbProvider">Database context factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="queryHelpers">Jellyfin's own query builder (access filtering).</param>
    /// <param name="itemTypeLookup">Maps BaseItemKind to the stored type name.</param>
    /// <param name="logger">Logger.</param>
    public PathSearchProvider(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IItemQueryHelpers queryHelpers,
        IItemTypeLookup itemTypeLookup,
        ILogger<PathSearchProvider> logger)
    {
        _dbProvider = dbProvider;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queryHelpers = queryHelpers;
        _itemTypeLookup = itemTypeLookup;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Feature Enhance";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 200;

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query) =>
        !string.IsNullOrWhiteSpace(query.SearchTerm) && query.SearchTerm.Trim().Length >= 2;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        var rawTerm = query.SearchTerm.Trim();
        var term = PathMatcher.Normalize(rawTerm);
        if (term.Length < 2)
        {
            return [];
        }

        var tokens = rawTerm
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length == 0)
        {
            return [];
        }

        var user = query.UserId is { } userId && userId != Guid.Empty ? _userManager.GetUserById(userId) : null;
        var accessFilter = BuildAccessFilter(user, query);
        var typeNames = MapKindsToTypeNames(query.IncludeItemTypes);
        var excludeTypeNames = MapKindsToTypeNames(query.ExcludeItemTypes);
        var parentId = query.ParentId is { } pid && pid != Guid.Empty ? pid : (Guid?)null;

        var hits = new Dictionary<Guid, Hit>();

        await using (var db = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var dbQuery = db.BaseItems
                .AsNoTracking()
                .Where(e => e.Path != null && e.Path != string.Empty && !e.IsVirtualItem && !e.IsFolder);

            dbQuery = ApplyScope(dbQuery, typeNames, excludeTypeNames, parentId);

            if (accessFilter is not null)
            {
                dbQuery = _queryHelpers.ApplyAccessFiltering(db, dbQuery, accessFilter);
            }

            foreach (var token in tokens)
            {
                var like = "%" + PathMatcher.EscapeLike(token) + "%";
                var cleanToken = PathMatcher.Normalize(token);
                dbQuery = dbQuery.Where(e =>
                    EF.Functions.Like(e.Path!, like, "\\")
                    || (cleanToken != string.Empty && e.CleanName != null && e.CleanName.Contains(cleanToken)));
            }

            // 确定性候选集：不加 ORDER BY 的行序只是碰巧稳定（EF 也会警告）
            var rows = await dbQuery
                .OrderBy(e => e.Id)
                .Take(MaxCandidates)
                .Select(e => new { e.Id, e.Path, e.CleanName, e.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                var score = ScoreCandidate(row.Path!, row.CleanName, row.Name, term);
                if (score > 0f)
                {
                    Add(hits, row.Id, score, DirectoryOf(row.Path!));
                }
            }

            if (hits.Count < 50)
            {
                await AddFuzzyFolderMatchesAsync(
                    db,
                    term,
                    accessFilter,
                    typeNames,
                    excludeTypeNames,
                    parentId,
                    hits,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var limit = query.Limit is > 0 ? query.Limit.Value : 100;
        var results = Diversify(hits, limit);

        _logger.LogDebug(
            "Feature Enhance matched {Candidates} candidates in {Folders} folders for '{Term}', returning {Count}",
            hits.Count,
            hits.Values.Select(h => h.Directory).Distinct(StringComparer.Ordinal).Count(),
            rawTerm,
            results.Count);

        return results;
    }

    /// <summary>
    /// 原生口径的访问过滤器（库访问 + 分级 + 标签），用法同
    /// <c>Emby.Server.Implementations.Library.Search.SearchQueryAccessFilter</c>。
    /// </summary>
    private InternalItemsQuery? BuildAccessFilter(User? user, SearchProviderQuery query)
    {
        if (user is null)
        {
            return null;
        }

        var accessFilter = new InternalItemsQuery(user)
        {
            IncludeItemTypes = query.IncludeItemTypes,
            ExcludeItemTypes = query.ExcludeItemTypes,
            IncludeItemsByName = !query.ParentId.HasValue || query.ParentId.Value == Guid.Empty
        };

        _libraryManager.ConfigureUserAccess(accessFilter, user);
        return accessFilter;
    }

    /// <summary>
    /// 调用方给的类型/父级作用域（与 SqlSearchProvider 的语义一致：有 include 时 include 说了算）。
    /// </summary>
    private static IQueryable<BaseItemEntity> ApplyScope(
        IQueryable<BaseItemEntity> query,
        List<string> typeNames,
        List<string> excludeTypeNames,
        Guid? parentId)
    {
        if (typeNames.Count > 0)
        {
            query = query.Where(e => typeNames.Contains(e.Type));
        }
        else if (excludeTypeNames.Count > 0)
        {
            query = query.Where(e => !excludeTypeNames.Contains(e.Type));
        }

        if (parentId.HasValue)
        {
            var parent = parentId.Value;
            query = query.Where(e => e.ParentId == parent || e.Parents!.Any(p => p.ParentItemId == parent));
        }

        return query;
    }

    private List<string> MapKindsToTypeNames(BaseItemKind[] kinds)
    {
        var list = new List<string>();
        foreach (var kind in kinds)
        {
            if (_itemTypeLookup.BaseItemKindNames.TryGetValue(kind, out var name) && !string.IsNullOrEmpty(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    private static void Add(Dictionary<Guid, Hit> hits, Guid id, float score, string directory)
    {
        if (!hits.TryGetValue(id, out var existing) || score > existing.Score)
        {
            hits[id] = new Hit(score, directory);
        }
    }

    private static string DirectoryOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index > 0 ? path[..index] : path;
    }

    /// <summary>
    /// Spreads the result page across every matching folder: inside one folder each further
    /// item loses a little score, so a folder holding thousands of matches cannot swallow the
    /// whole page while same-named folders elsewhere stay invisible. Jellyfin re-sorts hints by
    /// score, so this has to live in the score itself rather than in the ordering.
    /// </summary>
    private static IReadOnlyList<SearchResult> Diversify(Dictionary<Guid, Hit> hits, int limit)
    {
        var byDirectory = new Dictionary<string, List<KeyValuePair<Guid, Hit>>>(StringComparer.Ordinal);
        foreach (var pair in hits)
        {
            if (!byDirectory.TryGetValue(pair.Value.Directory, out var list))
            {
                list = [];
                byDirectory[pair.Value.Directory] = list;
            }

            list.Add(pair);
        }

        var results = new List<SearchResult>(hits.Count);
        foreach (var list in byDirectory.Values)
        {
            var ordered = list.OrderByDescending(pair => pair.Value.Score).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var penalty = Math.Min(i, DiversityMaxSteps) * DiversityPenaltyStep;
                results.Add(new SearchResult(ordered[i].Key, ordered[i].Value.Score - penalty));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.ItemId)
            .Take(limit)
            .ToArray();
    }

    private static float ScoreCandidate(string path, string? cleanName, string? name, string term)
    {
        var best = PathMatcher.ScoreName(
            string.IsNullOrEmpty(cleanName) ? PathMatcher.Normalize(name) : cleanName,
            term);

        var segments = PathMatcher.Segments(path);
        foreach (var segment in segments)
        {
            best = Math.Max(best, PathMatcher.ScoreSegment(segment, term));
        }

        if (segments.Length > 0
            && string.Join(' ', segments).Contains(term, StringComparison.Ordinal))
        {
            best = Math.Max(best, 56f);
        }

        return best;
    }

    private async Task AddFuzzyFolderMatchesAsync(
        JellyfinDbContext db,
        string term,
        InternalItemsQuery? accessFilter,
        List<string> typeNames,
        List<string> excludeTypeNames,
        Guid? parentId,
        Dictionary<Guid, Hit> hits,
        CancellationToken cancellationToken)
    {
        var folderQuery = db.BaseItems
            .AsNoTracking()
            .Where(e => e.IsFolder && e.Path != null && e.Path != string.Empty && !e.IsVirtualItem);

        if (accessFilter is not null)
        {
            folderQuery = _queryHelpers.ApplyAccessFiltering(db, folderQuery, accessFilter);
        }

        var folders = await folderQuery
            .OrderBy(e => e.Id)
            .Take(MaxFoldersScanned)
            .Select(e => new { e.Path })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var matched = new List<(string Path, float Score)>();
        foreach (var folder in folders)
        {
            var segments = PathMatcher.Segments(folder.Path!);
            if (segments.Length == 0)
            {
                continue;
            }

            var score = PathMatcher.ScoreSegment(segments[^1], term);
            if (score >= 34f)
            {
                matched.Add((folder.Path!, score));
            }
        }

        if (matched.Count == 0)
        {
            return;
        }

        foreach (var (path, score) in matched.OrderByDescending(m => m.Score).Take(MaxFuzzyFolders))
        {
            var prefix = PathMatcher.EscapeLike(path) + "/%";
            var itemQuery = db.BaseItems
                .AsNoTracking()
                .Where(e => e.Path != null
                    && !e.IsVirtualItem
                    && !e.IsFolder
                    && EF.Functions.Like(e.Path!, prefix, "\\"));

            itemQuery = ApplyScope(itemQuery, typeNames, excludeTypeNames, parentId);

            if (accessFilter is not null)
            {
                itemQuery = _queryHelpers.ApplyAccessFiltering(db, itemQuery, accessFilter);
            }

            var items = await itemQuery
                .OrderBy(e => e.Id)
                .Take(FallbackItemsPerFolder)
                .Select(e => e.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var itemScore = Math.Max(score - FolderFuzzyPenalty, 20f);
            foreach (var id in items)
            {
                Add(hits, id, itemScore, path);
            }
        }
    }

    private readonly record struct Hit(float Score, string Directory);
}
