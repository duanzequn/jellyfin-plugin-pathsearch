/*
 * Feature Enhance —— 搜索页 = 四个分类方格；点进去是【原生列表页】
 * ===========================================================================
 * 原生搜索页没有分页：一个 /Items?…&searchTerm=…&limit=800 回来就把 900 多张卡片
 * 一次性建出来（桌面卡顿、手机卡死），而且是横向滚动条，"什么都能滑出来"。
 *
 * 这一版把搜索页改成"分类入口"：
 *
 *   ┌───────────┬───────────┐
 *   │  视频     │  图片      │      搜索只查"每类有多少条"（一次 GROUP BY），
 *   │  49,568   │  46,957   │      不渲染任何卡片 —— 搜索页永远不会卡。
 *   ├───────────┼───────────┤
 *   │  相册     │  文件夹     │      点任意一格 → 进【原生列表页】#/list?type=…
 *   │  13       │  78       │      分页 / 排序 / 筛选 / 全部播放 全是原生的。
 *   └───────────┴───────────┘
 *
 * 原生列表页那一步有个坑：它的数据来自 /Items?searchTerm=…，而服务端只要带了
 * searchTerm 就走"搜索提供者"，TotalRecordCount 被 limit×3 卡死（实测 limit=100
 * 时总数只有 300，翻到第 4 页就空了）。所以这里给原生 ApiClient.getItems 加了一层
 * 桥：列表页要第几页，就用插件接口（数据库 COUNT + Skip/Take）取那一页的 id 和
 * **真实总数**，再用原生接口按 id 取回完整 DTO —— 卡片、排序、筛选、分页器全都还是
 * 原生的那套，只是数字对了、翻到底也不会空。
 *
 * 兜底：拿不到 QueryClient 或取数失败时都不隐藏原生结果，搜索照常可用。
 *
 * 依赖：服务端 FeatureEnhance 插件（本脚本由它注入）
 */
(function () {
    'use strict';

    var STYLE_ID = 'feature-enhance-search-style';
    var COUNTS_TTL = 5 * 60 * 1000;      // 同一个词的计数结果缓存 5 分钟（返回搜索页时 0 请求）
    var COUNTS_KEY = 'featureEnhanceCounts:';
    var LIST_LIMIT_MAX = 500;      // 与服务端 MaxLimit 一致
    // 按 id 回原生接口取详情时每批多少个。一个 GUID 是 32 字符 + 逗号，Kestrel 的请求行上限是 8KB：
    // 300 个 id ≈ 10KB → HTTP 414 URI Too Long，promise 被拒 → 播放/随机播放"点了没反应"（实测踩到过）。
    // 40 个一批 ≈ 1.4KB，稳。
    var IDS_PER_REQUEST = 40;

    // 条目级筛选（播放状态/分辨率/字幕/标签/字母…）。**文件夹格子不套这些**：
    // 文件夹是容器，用户搜的是"哪些文件夹"，而且实测列表页会自发带上一个 IsFavorite
    // （筛选按钮上还会显示角标 1），一旦套到"按 id 取详情"上，9 个文件夹就变 0 张卡片。
    // 关键是三处口径必须一致：插件查询、按 id 取详情、播放下钻。
    var ITEM_FILTER_KEYS = ['Filters', 'IsFavorite', 'Is4K', 'IsHD', 'Is3D', 'HasSubtitles', 'HasTrailer',
        'HasSpecialFeature', 'HasThemeSong', 'HasThemeVideo', 'VideoTypes', 'GenreIds', 'Tags',
        'NameStartsWith', 'NameLessThan', 'SeriesStatus'];

    function stripItemFilters(opts) {
        for (var i = 0; i < ITEM_FILTER_KEYS.length; i++) { delete opts[ITEM_FILTER_KEYS[i]]; }
        return opts;
    }

    // 四个方格：key -> 类型集合
    var TILES = [
        { key: 'video', types: ['Video', 'Movie', 'Episode', 'Series'], zh: '视频', en: 'Videos', icon: 'movie' },
        { key: 'photo', types: ['Photo'], zh: '图片', en: 'Images', icon: 'photo' },
        { key: 'album', types: ['PhotoAlbum'], zh: '相册', en: 'Albums', icon: 'photo_album' },
        { key: 'folder', types: ['Folder'], zh: '文件夹', en: 'Folders', icon: 'folder', folder: true }
    ];
    // 计数接口按"分组"给数字：每组的类型集合必须和点进去时列表页查的类型完全一致，
    // 这样"方格上的数字"才等于"列表里的条数"（@folders 表示所有文件夹）
    var TILE_GROUPS = [
        'Video,Movie,Episode,Series',
        'Photo',
        'PhotoAlbum',
        '@folders'
    ];

    var session = null;
    var requestSeq = 0;
    var listBridge = { installed: false, context: null, active: false };

    // "文件夹"格子专用：原生的 播放/随机播放/加入队列 会再走一次 getItems 拿条目，
    // 但那次拿到的是**文件夹**（播放器放不了）。点按钮时在这里打个标记，
    // 桥看到标记就把这次请求换成"文件夹里的可播放内容"。标记只在同一个 tick 内有效。
    var pendingPlayIntent = null;
    var PLAY_BUTTON_SELECTOR = '.btnPlay, .btnShuffle, .btnQueue';

    // ── 样式 ─────────────────────────────────────────────────────────────
    function ensureStyle() {
        if (document.getElementById(STYLE_ID) || !document.head) { return; }
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = [
            '#fe-search-results{padding-bottom:3em;}',
            '#fe-search-results .fe-summary{opacity:.72;font-size:.92em;padding:.6em 1.2em 0;}',
            '#fe-search-results .fe-tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(12em,1fr));gap:1.1em;padding:1em 1.2em 0;}',
            '#fe-search-results .fe-tile{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:.3em;'
            + 'padding:1.2em .8em;border:0;border-radius:1em;background:rgba(127,127,127,.14);color:inherit;cursor:pointer;'
            + 'font:inherit;transition:background .15s ease,transform .15s ease;touch-action:manipulation;}',
            '#fe-search-results .fe-tile:hover:not([disabled]){background:rgba(127,127,127,.24);transform:translateY(-2px);}',
            '#fe-search-results .fe-tile[disabled]{opacity:.38;cursor:default;}',
            '#fe-search-results .fe-tile-icon{font-size:1.7em;opacity:.7;line-height:1;}',
            '#fe-search-results .fe-tile-count{font-size:1.9em;font-weight:700;line-height:1.15;font-variant-numeric:tabular-nums;}',
            '#fe-search-results .fe-tile-label{opacity:.7;font-size:.95em;}',
            '#fe-search-results .fe-empty,#fe-search-results .fe-loading{opacity:.75;padding:2em 1.2em;display:flex;gap:.8em;align-items:center;flex-wrap:wrap;}',
            '#fe-search-results .fe-retry,#fe-search-results .fe-native{border:0;border-radius:2em;padding:.5em 1.1em;'
            + 'background:rgba(127,127,127,.2);color:inherit;font:inherit;cursor:pointer;touch-action:manipulation;}',
            // 搜索路由上立刻把原生结果区藏掉（body 类在 hashchange 里同步加上，
            // 早于 React 渲染），否则会先闪一下原生搜索列表再切成四个方格
            'body.fe-searching .searchResults,body.fe-searching .noItemsMessage{display:none !important;}',
            // 原生搜索页的加载层（Loading.show() 插入的 .docspinner 等）一律压掉：
            // 原生那套由 React Query 的 isPending 驱动，而 isPending 在"查询被禁用"时也是 true，
            // 所以它可以永远挂着转圈 —— 结果就是我们四个方格上面顶着一个转不完的圈。
            // 搜索页现在完全由我们接管，原生加载指示没有任何存在的理由。
            'body.fe-searching .docspinner,'
            + 'body.fe-searching .mdl-spinner,'
            + 'body.fe-searching #searchPage .spinner,'
            + 'body.fe-searching #searchPage .MuiCircularProgress-root{display:none !important;}',
            '@media (max-width:600px){'
            + '#fe-search-results .fe-tiles{grid-template-columns:repeat(2,1fr);gap:.8em;padding:.8em .8em 0;}'
            + '#fe-search-results .fe-tile{padding:1em .6em;}'
            + '#fe-search-results .fe-tile-count{font-size:1.6em;}'
            + '#fe-search-results .fe-summary{padding:.5em .8em 0;}}'
        ].join('');
        document.head.appendChild(style);
    }

    var styleObserverInstalled = false;

    function guardStyle() {
        ensureStyle();
        if (styleObserverInstalled || typeof MutationObserver !== 'function' || !document.head) { return; }
        styleObserverInstalled = true;
        new MutationObserver(ensureStyle).observe(document.head, { childList: true });
    }

    function lang() {
        return (navigator.language || 'en').toLowerCase().indexOf('zh') === 0 ? 'zh' : 'en';
    }

    function label(tile) { return lang() === 'zh' ? tile.zh : tile.en; }

    // ── 小工具 ───────────────────────────────────────────────────────────
    function accessToken() {
        try {
            if (window.ApiClient && typeof window.ApiClient.accessToken === 'function') {
                var t = window.ApiClient.accessToken();
                if (t) { return t; }
            }
        } catch (e) { /* ignore */ }
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = creds.Servers || [];
            for (var i = 0; i < servers.length; i++) { if (servers[i].AccessToken) { return servers[i].AccessToken; } }
        } catch (e) { /* ignore */ }
        return '';
    }

    function userId() {
        try {
            if (window.ApiClient && typeof window.ApiClient.getCurrentUserId) { return window.ApiClient.getCurrentUserId(); }
        } catch (e) { /* ignore */ }
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var servers = creds.Servers || [];
            for (var i = 0; i < servers.length; i++) { if (servers[i].UserId) { return servers[i].UserId; } }
        } catch (e) { /* ignore */ }
        return '';
    }

    function serverId() {
        try {
            var creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
            var s = (creds.Servers || [])[0];
            if (s && s.Id) { return s.Id; }
        } catch (e) { /* ignore */ }
        try { if (window.ApiClient && window.ApiClient.serverId) { return window.ApiClient.serverId(); } } catch (e) { /* ignore */ }
        return '';
    }

    function apiUrl(path, params) {
        var url = null;
        try { if (window.ApiClient && window.ApiClient.getUrl) { url = window.ApiClient.getUrl(path, params); } } catch (e) { /* ignore */ }
        if (!url) {
            var parts = [];
            Object.keys(params || {}).forEach(function (k) {
                if (params[k] === undefined || params[k] === null || params[k] === '') { return; }
                parts.push(k + '=' + encodeURIComponent(params[k]));
            });
            url = '/' + path.replace(/^\//, '') + (parts.length ? '?' + parts.join('&') : '');
        }
        var t = accessToken();
        if (t && url.indexOf('ApiKey=') === -1) { url += (url.indexOf('?') === -1 ? '?' : '&') + 'ApiKey=' + encodeURIComponent(t); }
        return url;
    }

    function getJson(url) {
        return fetch(url, { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : null; }, function () { return null; });
    }

    function esc(text) {
        return String(text == null ? '' : text)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function field(obj, name) {
        if (!obj) { return undefined; }
        if (obj[name] !== undefined && obj[name] !== null) { return obj[name]; }
        var lower = name.charAt(0).toLowerCase() + name.slice(1);
        return obj[lower] !== undefined && obj[lower] !== null ? obj[lower] : undefined;
    }

    function formatCount(value) {
        try { return Number(value || 0).toLocaleString(); } catch (e) { return String(value || 0); }
    }

    function currentQuery() {
        var hash = location.hash || '';
        if (hash.indexOf('/search') === -1) { return null; }
        var m = /[?&]query=([^&]*)/.exec(hash);
        if (!m) { return null; }
        try {
            var v = decodeURIComponent(m[1].replace(/\+/g, ' ')).trim();
            return v.length >= 1 ? v : null;
        } catch (e) { return null; }
    }

    // 注：以前这里还有一套"从 React fiber 上找 QueryClient、把原生搜索的分节裁到 2 条"的兜底。
    // 现在原生搜索分节永远是空的（请求被本地短路，见 Bootstrap），那段既没用又脆（遍历 fiber、
    // 写 React Query 缓存），已经删掉。
    // ── 搜索页：四个方格 ─────────────────────────────────────────────────
    var hostContainerRef = null;

    function ensureContainer() {
        if (hostContainerRef && hostContainerRef.isConnected) { return hostContainerRef; }
        Array.prototype.forEach.call(document.querySelectorAll('#fe-search-results'), function (node) {
            if (node !== hostContainerRef && node.parentNode) { node.parentNode.removeChild(node); }
        });
        var page = document.getElementById('searchPage')
            || (document.querySelector('.searchResults') || {}).parentElement
            || document.querySelector('.searchPage');
        if (!page) { return null; }
        ensureStyle();
        if (!hostContainerRef) {
            hostContainerRef = document.createElement('div');
            hostContainerRef.id = 'fe-search-results';
        }
        page.appendChild(hostContainerRef);
        return hostContainerRef;
    }

    // 用 body 上的类来隐藏原生结果区：CSS 会在元素出现的**同一帧**生效，
    // 比"等 React 渲染完再给元素加 style"早得多（踩过：会先闪一下原生列表）
    function setSearching(on) {
        if (!document.body) { return; }
        if (on) { document.body.classList.add('fe-searching'); }
        else { document.body.classList.remove('fe-searching'); }
    }

    function hideNative() { setSearching(true); }

    function showNative() { setSearching(false); }

    function removeUi() {
        if (hostContainerRef && hostContainerRef.parentNode) { hostContainerRef.parentNode.removeChild(hostContainerRef); }
        hostContainerRef = null;
        session = null;
        showNative();
    }

    function tileHtml(tile, count) {
        return '<button type="button" class="fe-tile" data-fe-tile="' + esc(tile.key) + '"' + (count > 0 ? '' : ' disabled') + '>'
            + '<span class="material-icons fe-tile-icon" aria-hidden="true">' + esc(tile.icon) + '</span>'
            + '<span class="fe-tile-count">' + esc(formatCount(count)) + '</span>'
            + '<span class="fe-tile-label">' + esc(label(tile)) + '</span>'
            + '</button>';
    }

    // 原生搜索请求的短路开关（由 head 里的引导脚本安装，见 ClientInjectionStartupFilter.Bootstrap）。
    // 正常情况下 'off'：搜索页那些 /Items?searchTerm= 请求会被直接回空、不打服务器。
    // 一旦我们自己的接口挂了就置成 'on'，让原生搜索重新可用（"改用原生搜索"按钮还会刷新一次页面）。
    // 注意：这里**故意没有**任何"退回原生搜索"的开关。
    // 搜索页完全由本插件接管，原生搜索请求永久短路（见 ClientInjectionStartupFilter.Bootstrap），
    // 接口挂了就显示 0 条 + 重试，绝不把页面交回原生。

    // 接口取不到数：照样画四个 0，只多一行提示 + 重试；
    // 绝不把页面交回原生搜索（原生请求也一直被短路着，交回去只会是一片空白）。
    function renderError(container, term) {
        var tiles = TILES.map(function (tile) { return { tile: tile, count: 0, match: 'name' }; });
        session.tiles = tiles;
        container.innerHTML = '<div class="fe-summary">搜索「' + esc(term) + '」· 共 0 条结果</div>'
            + '<div class="fe-tiles">' + tiles.map(function (entry) { return tileHtml(entry.tile, 0); }).join('') + '</div>'
            + '<div class="fe-empty">插件接口没有响应，结果按 0 显示。'
            + '<button type="button" class="fe-retry" data-fe-retry="1">重试</button></div>';
        hideNative();
    }

    function renderTiles(container, term, counts) {
        var total = 0;
        var tiles = TILES.map(function (tile, index) {
            var group = (counts.groups && counts.groups[index]) || { total: 0, match: 'name' };
            total += group.total;
            return { tile: tile, count: group.total, match: group.match };
        });

        var body = tiles.map(function (entry) { return tileHtml(entry.tile, entry.count); }).join('');
        container.innerHTML = '<div class="fe-summary">搜索「' + esc(term) + '」· 共 ' + esc(formatCount(total)) + ' 条结果</div>'
            + '<div class="fe-tiles">' + body + '</div>';

        session.tiles = tiles;
    }

    function onContainerClick(event) {
        var retry = event.target.closest ? event.target.closest('[data-fe-retry]') : null;
        if (retry) {
            event.preventDefault();
            event.stopPropagation();
            if (session) { build(session.term); }
            return;
        }

        var tileNode = event.target.closest ? event.target.closest('[data-fe-tile]') : null;
        if (!tileNode || !hostContainerRef || !hostContainerRef.contains(tileNode)) { return; }
        event.preventDefault();
        event.stopPropagation();
        if (tileNode.disabled) { return; }

        var key = tileNode.getAttribute('data-fe-tile');
        var entry = (session && session.tiles || []).filter(function (t) { return t.tile.key === key; })[0];
        if (!entry || !entry.count) { return; }

        var match = entry.match || 'name';   // 这一格是用哪一级命中的（name / path / fuzzy）

        // 搜索词/口径写进 URL：会话存储会有 30 分钟过期、换标签页就丢，而路由里没有 searchTerm，
        // 一旦丢了列表页会静默变成"整个媒体库"（踩过）。URL 才是唯一可靠的上下文载体。
        var type = entry.tile.types.join(',');
        var sid = serverId();
        var route = '#/list?type=' + encodeURIComponent(type)
            + '&feTerm=' + encodeURIComponent(session.term)
            + '&feMatch=' + encodeURIComponent(match)
            + (sid ? '&serverId=' + encodeURIComponent(sid) : '');
        var ep = window.Emby && window.Emby.Page;
        if (ep && typeof ep.show === 'function') { ep.show(route.replace(/^#\/?/, '')); } else { location.hash = route; }
    }

    var clickBound = null;
    function bindContainer(container) {
        if (clickBound === container) { return; }
        clickBound = container;
        container.addEventListener('click', onContainerClick, true);
    }

    // 两次 GROUP BY（item / folder）就够了。以前文件夹那条走的是 kind=folder 的完整查询，
    // 名称一条都匹配不上时还会跑"模糊匹配"扫两万行 —— 慢的时候能到几秒，
    // 以前那个"4 秒兜底放出原生结果"就会把原生列表闪出来（用户偶发看到的就是这个）。
    // 一次请求拿回四个方格的总数：每组还会回传"是用名称/路径/模糊哪一级命中的"，
    // 点进去时列表页用同一级查询，数字才对得上（模糊搜索就是靠这条链路兜底的）
    // 计数缓存：一个搜索词只查一次。从列表页/详情页返回来时直接渲染，
    // 不再打第二次 /Search/Counts（计数变化很慢，5 分钟足够）。
    function countsCacheGet(term) {
        try {
            var raw = sessionStorage.getItem(COUNTS_KEY + term);
            if (!raw) { return null; }
            var entry = JSON.parse(raw);
            if (!entry || !entry.at || !entry.groups || Date.now() - entry.at > COUNTS_TTL) { return null; }
            return { groups: entry.groups };
        } catch (e) { return null; }
    }

    function countsCachePut(term, result) {
        try {
            sessionStorage.setItem(COUNTS_KEY + term, JSON.stringify({ at: Date.now(), groups: result.groups }));
        } catch (e) { /* 配额满了就算了 */ }
    }

    function loadCounts(term) {
        return getJson(apiUrl('FeatureEnhance/Search/Counts', {
            term: term,
            groups: TILE_GROUPS.join(';'),
            userId: userId()
        })).then(function (data) {
            if (!data) { return null; }
            var groups = field(data, 'Groups');
            if (!groups || !groups.length) { return null; }
            return {
                groups: groups.map(function (group) {
                    return { total: field(group, 'Total') || 0, match: field(group, 'Match') || 'name' };
                })
            };
        });
    }

    function build(term) {
        var container = ensureContainer();
        if (!container) { return; }

        requestSeq++;
        var mySeq = requestSeq;
        session = { term: term, container: container, tiles: [], match: 'name' };

        var cached = countsCacheGet(term);
        if (cached) {
            renderTiles(container, term, cached);
            hideNative();
            return;
        }

        container.innerHTML = '<div class="fe-loading">正在搜索「' + esc(term) + '」…</div>';

        loadCounts(term).then(function (result) {
            if (mySeq !== requestSeq || !session) { return; }
            if (!result) {
                renderError(container, term);
                return;
            }
            countsCachePut(term, result);
            renderTiles(container, term, result);
            hideNative();
        }, function () {
            if (mySeq !== requestSeq || !session) { return; }
            renderError(container, term);
        });
    }

    var pending = null;
    function schedule(delay) {
        if (pending) { clearTimeout(pending); }
        pending = setTimeout(function () { pending = null; sync(); }, typeof delay === 'number' ? delay : 220);
    }

    function sync() {
        var term = currentQuery();
        if (!term) {
            setSearching(false);
            removeUi();
            return;
        }
        setSearching(true);      // 只要在搜索路由上就藏掉原生结果区（避免闪一下）
        var container = ensureContainer();
        if (!container) { return; }
        bindContainer(container);
        guardStyle();

        if (session && session.term === term && session.container === container && container.isConnected) {
            hideNative();
            return;
        }
        if (session && session.term === term && session.tiles.length && session.container !== container) {
            session.container = container;
            var tiles = session.tiles;
            var total = tiles.reduce(function (sum, entry) { return sum + entry.count; }, 0);
            container.innerHTML = '<div class="fe-summary">搜索「' + esc(term) + '」· 共 ' + esc(formatCount(total)) + ' 条结果</div>'
                + '<div class="fe-tiles">' + tiles.map(function (entry) { return tileHtml(entry.tile, entry.count); }).join('') + '</div>';
            hideNative();
            return;
        }
        build(term);
    }

    // ── 原生列表页桥接 ───────────────────────────────────────────────────
    function isListRoute() {
        return (location.hash || '').indexOf('/list') !== -1;
    }

    // 列表页自己的 type 参数，必须和当前搜索上下文一致（否则就是普通浏览文件夹，不该接管）
    function listTypeFromHash() {
        var m = /[?&]type=([^&]*)/.exec(location.hash || '');
        if (!m) { return null; }
        try { return decodeURIComponent(m[1]); } catch (e) { return null; }
    }

    // 列表页路由里带的搜索上下文（首选来源）
    function contextFromHash() {
        var hash = location.hash || '';
        var m = /[?&]feTerm=([^&]*)/.exec(hash);
        if (!m) { return null; }
        var term;
        try { term = decodeURIComponent(m[1].replace(/\+/g, ' ')); } catch (e) { return null; }
        if (!term) { return null; }

        var types = String(listTypeFromHash() || '').split(',')
            .map(function (s) { return s.trim(); })
            .filter(Boolean);
        if (!types.length) { return null; }

        var match = 'name';
        var mm = /[?&]feMatch=([^&]*)/.exec(hash);
        if (mm) { try { match = decodeURIComponent(mm[1]) || 'name'; } catch (e) { /* ignore */ } }

        return {
            term: term,
            match: match,
            types: types,
            folder: types.length === 1 && types[0].toLowerCase() === 'folder',
            at: Date.now()
        };
    }

    // 捕获阶段挂在 document 上：一定早于列表页自己绑在按钮上的 click 处理器，
    // 所以标记肯定能在它调用 getItems 之前设好。
    function watchPlayButtons() {
        document.addEventListener('click', function (event) {
            if (pendingPlayIntent) { return; }
            var target = event.target;
            if (!target || typeof target.closest !== 'function') { return; }
            var button = target.closest(PLAY_BUTTON_SELECTOR);
            if (!button) { return; }

            var context = bridgeContext();
            if (!context || !context.folder) { return; }

            var intent = button.classList.contains('btnShuffle') ? 'shuffle'
                : (button.classList.contains('btnQueue') ? 'queue' : 'play');
            pendingPlayIntent = intent;
            // 兜底：原生没在同一 tick 内取数（理论上不会）就自己清掉，别影响后面的分页请求
            setTimeout(function () {
                if (pendingPlayIntent === intent) { pendingPlayIntent = null; }
            }, 1000);
        }, true);
    }

    function installListBridge() {
        if (listBridge.installed) { return true; }
        var api = window.ApiClient;
        if (!api || typeof api.getItems !== 'function') { return false; }

        var original = api.getItems;
        var patched = function (user, options) {
            // 每次都按"当前路由"现算上下文：路由切换和列表页首发请求的顺序没有保证，
            // 用缓存的上下文会出现"新列表用上一个格子的类型发请求"（踩过：切到视频格子却先按
            // 文件夹查了一次，于是分页器停在 1、卡片还多出一张旧的）
            var context = bridgeContext();
            if (!context || !options || options.Ids) {
                return original.call(this, user, options);
            }

            var opts = Object.assign({}, options);
            delete opts.SearchTerm;      // 别再触发服务端那套"提供者"查询
            var startIndex = Number(opts.StartIndex) || 0;
            var limit = Math.min(Number(opts.Limit) || 100, LIST_LIMIT_MAX);
            var itemTypes = opts.IncludeItemTypes || context.types.join(',');

            // 刚才点了播放/随机播放/加入队列（只对"文件夹"格子有意义）
            if (pendingPlayIntent && context.folder) {
                var intent = pendingPlayIntent;
                pendingPlayIntent = null;
                return fetchPlayable(context, intent, api, user, original, opts, limit);
            }

            var url = apiUrl('FeatureEnhance/Search', {
                term: context.term,
                kind: context.folder ? 'folder' : 'item',
                itemTypes: context.folder ? undefined : itemTypes,
                // 口径必须和"方格上的数字"完全一致：用计数接口回传的那一级（name / path / fuzzy）
                match: context.match || 'name',
                filters: context.folder ? undefined : opts.Filters,
                startIndex: startIndex,
                limit: limit,
                // 原生排序键原样传（服务端按 ItemSortBy 解析），不再只映射 4 种
                sortBy: opts.SortBy,
                sortOrder: opts.SortOrder,
                // 原生的其余筛选条件也原样传：服务端用 Jellyfin 自己的 TranslateQuery 翻译，
                // 以前只透传 4 个用户数据筛选，"按 4K/字幕/类型筛选"点了没反应
                videoTypes: opts.VideoTypes,
                isHd: opts.IsHD,
                is4K: opts.Is4K,
                is3D: opts.Is3D,
                hasSubtitles: opts.HasSubtitles,
                hasTrailer: opts.HasTrailer,
                hasThemeSong: opts.HasThemeSong,
                hasThemeVideo: opts.HasThemeVideo,
                hasSpecialFeature: opts.HasSpecialFeature,
                tags: opts.Tags,
                genres: opts.Genres,
                nameStartsWith: opts.NameStartsWith,
                nameLessThan: opts.NameLessThan,
                userId: userId()
            });

            return getJson(url).then(function (page) {
                var rows = (page && field(page, 'Items')) || [];
                var total = (page && field(page, 'Total')) || rows.length;
                if (!rows.length) { return { Items: [], TotalRecordCount: total, StartIndex: startIndex }; }

                var ids = rows.map(function (row) { return field(row, 'Id'); });
                if (context.folder) {
                    // 文件夹那条查询里"文件夹"还包含相册目录等，而列表页路由的 type=Folder
                    // 会把这个 id 集合再过滤一次（数量和列表就对不上了），这里去掉类型过滤，
                    // 交给插件接口的口径决定
                    delete opts.IncludeItemTypes;
                    // 条目级筛选也必须一起去掉：插件的文件夹查询本来就没套它们，
                    // 只在按 id 取详情时套 → 列表直接空掉（9 个文件夹变 0 张卡片）
                    stripItemFilters(opts);
                }

                return fetchByIds(original, api, user, opts, ids).then(function (ordered) {
                    return { Items: ordered, TotalRecordCount: total, StartIndex: startIndex };
                });
            }, function () { return original.call(api, user, options); });
        };

        try {
            api.getItems = patched;
            var proto = Object.getPrototypeOf(api);
            if (proto && typeof proto.getItems === 'function') { proto.getItems = patched; }
            listBridge.installed = true;
            return true;
        } catch (e) {
            return false;
        }
    }

    // 按 id 回原生接口取完整 DTO —— 分批发，避免一次塞几百个 GUID 把 URL 撑爆（414）。
    // opts 由调用方准备好（SearchTerm/IncludeItemTypes 该删的已经删掉），这里只补 Ids/分页。
    function fetchByIds(original, api, user, opts, ids) {
        var batches = [];
        for (var i = 0; i < ids.length; i += IDS_PER_REQUEST) {
            batches.push(ids.slice(i, i + IDS_PER_REQUEST));
        }

        return Promise.all(batches.map(function (batch) {
            var batchOpts = Object.assign({}, opts);
            batchOpts.Ids = batch.join(',');
            batchOpts.StartIndex = 0;
            batchOpts.Limit = batch.length;
            batchOpts.EnableTotalRecordCount = false;
            return Promise.resolve(original.call(api, user, batchOpts)).then(function (result) {
                return (result && result.Items) || [];
            }, function () {
                return [];
            });
        })).then(function (lists) {
            var byId = {};
            lists.forEach(function (items) {
                items.forEach(function (item) { byId[item.Id] = item; });
            });
            var ordered = ids.map(function (id) { return byId[id]; }).filter(Boolean);
            if (ids.length > 0 && ordered.length === 0) {
                // 请求了 id 却一条都没取回来：多半是某个筛选/类型参数把结果过滤光了。
                // 别静默失败（"播放点了没反应"当年就是这么来的），留一条线索。
                console.warn('[FeatureEnhance] 按 id 取详情返回 0 条（ids=' + ids.length + '），检查是否有多余的筛选参数');
            }
            return ordered;
        });
    }

    // "文件夹"格子：把播放请求换成"文件夹内部的可播放条目"。
    // 取数仍然走插件接口（同一套匹配口径 + 原生筛选 + 权限），再按 id 回原生接口拿完整 DTO，
    // 最后照旧由原生播放器（列表页里那个 A.f.play / queue）负责播放 —— 我们只改"喂什么给它"。
    function fetchPlayable(context, intent, api, user, original, opts, limit) {
        var url = apiUrl('FeatureEnhance/Search/Playable', {
            term: context.term,
            match: context.match || 'name',
            shuffle: intent === 'shuffle' ? 1 : 0,
            limit: Math.min(Math.max(limit, 1), LIST_LIMIT_MAX),
            sortBy: opts.SortBy,
            sortOrder: opts.SortOrder,
            // 与列表页同一口径：文件夹格子不套条目级筛选（否则带上那个自发的 IsFavorite
            // 就一条都取不到，表现就是"随机播放点了没反应"）
            userId: userId()
        });

        return getJson(url).then(function (page) {
            var rows = (page && field(page, 'Items')) || [];
            var ids = rows.map(function (row) { return field(row, 'Id'); }).filter(Boolean);
            if (!ids.length) { return { Items: [], TotalRecordCount: 0, StartIndex: 0 }; }

            var playOpts = Object.assign({}, opts);
            delete playOpts.SearchTerm;
            // 列表页的 type=Folder 会被原样带到这次按 id 取详情上，那会把结果全过滤掉
            delete playOpts.IncludeItemTypes;
            // 关键：条目级筛选也必须剥掉。列表页会自发带上一个 IsFavorite，
            // 留在这一跳就会把 300 条全过滤成 0 条 —— 播放器收到空列表，
            // 连 /PlaybackInfo 都不发，直接弹"无法找到有效的媒体来源来播放"。
            stripItemFilters(playOpts);

            return fetchByIds(original, api, user, playOpts, ids).then(function (ordered) {
                return { Items: ordered, TotalRecordCount: ordered.length, StartIndex: 0 };
            });
        }, function () {
            return { Items: [], TotalRecordCount: 0, StartIndex: 0 };
        });
    }

    // 当前路由是不是"由搜索格子打开的那个列表页"？是的话返回它的搜索上下文。
    // 上下文唯一的来源就是 URL（type= + feTerm= + feMatch=）：会话存储会有 30 分钟过期、
    // 换标签页就丢的问题，过期之后列表页会静默变成"整个媒体库"，所以那条路已经删了。
    function bridgeContext() {
        return isListRoute() ? contextFromHash() : null;
    }

    function syncDetail() {
        installListBridge();
    }

    // ── 启动 ─────────────────────────────────────────────────────────────

    function boot() {
        guardStyle();
        installListBridge();
        watchPlayButtons();
        syncDetail();
        // 同步先藏（不等 debounce），这样首屏直接进搜索页也不会闪原生列表
        if (currentQuery()) { setSearching(true); }
        else { setSearching(false); }
        schedule(120);
    }

    window.addEventListener('hashchange', function () {
        if (currentQuery()) { setSearching(true); }
        syncDetail();
        schedule(260);
    });
    document.addEventListener('DOMContentLoaded', boot);
    if (document.body) {
        new MutationObserver(function () {
            // 微任务里立刻补上隐藏类：即使某次导航没有触发 hashchange（app 内部用 pushState），
            // MutationObserver 也在浏览器绘制之前跑，原生结果区不会被画出来
            if (currentQuery()) { setSearching(true); }
            syncDetail();
            schedule();
        }).observe(document.body, { childList: true, subtree: true });
    }

    boot();
    console.log('[FeatureEnhance] 搜索分类方格 + 原生列表页分页桥接已启用');
})();
