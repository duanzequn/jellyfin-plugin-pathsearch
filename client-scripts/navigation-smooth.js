/*
 * Feature Enhance —— 页面切换"冻结帧"，消掉返回主页/上一级时的闪烁
 * ===========================================================================
 * 实测（1280x720、20fps 录屏逐帧统计，见 tools/docs/DEVELOPMENT.md）：
 *   点开条目 / 返回上一级 的瞬间，整页内容会被拆掉重建 —— 有 2~3 帧
 *   （100~150ms）**内容区完全空白**（只剩顶栏），随后卡片再逐张把图片画出来。
 *   这就是"页面会闪烁一下，好像是重新渲染了元素"。
 *   根因在客户端本身：路由切换时 React 卸载旧页面、重新 buildCards，再等图片
 *   解码/绘制，中间那段空窗期没东西可看 —— 容器里的文件我们一个都不改。
 *
 * 做法：路由一变，先给当前页面拍一张"冻结帧" —— cloneNode 一份可见页面挂到
 *   fixed 覆盖层上（实测 2200 个节点只要 ~3ms），让新页面在覆盖层后面慢慢渲染；
 *   等新页面真的画出内容了，再 160ms 淡出。用户看到的是
 *   "旧页面 → 新页面"的交叉过渡，而不是"旧页面 → 黑屏 → 新页面"。
 *
 * 细节：
 *   - cloneNode 不带 scrollTop/scrollLeft，按节点下标一一复制回去（含横向滚动条）；
 *   - 克隆出来的 iframe（首页大图区）会重新加载，直接换成同尺寸的底色块；
 *   - 覆盖层 z-index 低于顶栏（MUI AppBar 1100），顶栏保持可交互；
 *   - 用户一旦滚轮/触摸/按键，立刻撤掉冻结帧，绝不抢操作；
 *   - 同一条路由只是换了搜索词（#/search?query=…）不拍帧：那是原地更新。
 *
 * 依赖：服务端 FeatureEnhance 插件（本脚本由它注入）
 */
(function () {
    'use strict';

    var BG = '#101010';
    var TOUCH = ('ontouchstart' in window) || (navigator.maxTouchPoints > 0);
    var FADE_MS = 200;        // 淡出时长：新页面的卡片图片正好在这段时间里画完
    var SETTLE_MS = TOUCH ? 280 : 190;   // 新页面"有内容"之后再等这么久，避开"有卡片但图还没解码"的中间态（手机慢一点）
    var MAX_HOLD_MS = 1500;   // 新页面一直没有内容时的兜底
    var MIN_HOLD_MS = 40;

    var overlay = null;
    var overlaySource = null;
    var overlayStart = 0;
    var pollTimer = null;
    var lastRoute = null;
    var interacted = false;

    // ── 注入样式：图片淡入 / blurhash 占位都是"闪一下"的来源，直接压掉 ────
    function ensureStyle() {
        var id = 'feature-enhance-style';
        if (document.getElementById(id) || !document.head) { return; }
        var style = document.createElement('style');
        style.id = id;
        style.textContent = [
            // ── 主题（Abyss）自己的入场动画：每次路由切换都会整体重放 ──
            // .verticalSection:nth-child(n) 上挂着 abyss-section-fade-up 0.7s（错峰 0.05~0.45s），
            // 也就是每换一页，所有分区连同里面的图片、按钮都会从 opacity:0 + 下移 20px 重新淡入一遍
            // —— 这就是"整页元素闪来闪去"。我们已经有冻结帧做交叉过渡，这个必须关掉。
            '.verticalSection,.verticalSection:nth-child(n){animation:none !important;}',
            // 已评分的心形图标也挂着一个常驻动画（pop + glow），每次渲染都会重放
            '.ratingbutton-withrating .material-icons.favorite{animation:none !important;}',
            // 图片淡入：返回瞬间每张卡片都会淡入一次，累计起来就是"整页闪一下"
            '.lazy-image-fadein,.lazy-image-fadein-fast{animation:none !important;opacity:1 !important;}',
            // blurhash 占位图会先画一块模糊色、再被真图盖住 —— 又是一次闪
            '.blurhash-canvas{display:none !important;}',
            // 卡片/分区只在 transform/opacity 上做过渡：返回瞬间会有几百个元素
            // 同时做 width / scrollbar-color 过渡（实测 722 个），收窄后只剩个位数
            '.card{transition-property:transform,opacity,box-shadow !important;}',
            '.cardOverlayContainer{transition-property:opacity !important;}',
            '.itemsContainer{transition-property:none !important;}',
            // 冻结帧自己不要有动画
            '#feature-enhance-freeze,#feature-enhance-freeze *{animation:none !important;}',
            // ── 触屏上的"粘住 hover" ──
            // 实测：手指点过的按钮在之后再点别处，仍然 matches(':hover') 为 true
            // （pointer-events 开关、blur() 都清不掉），于是那个按钮永远是"高亮/变色"状态，
            // 在页面之间来回切换时看着就像"按钮突然变了个颜色"。
            // 这里只在触屏上、只对本来就是透明底色的按钮变体（text / icon / outlined）把 hover 背景还原，
            // 实心按钮（对话框里的主按钮）不动，避免把它们的底色弄坏。
            '@media (hover:none){'
            + '.MuiButtonBase-root.MuiButton-text:hover,'
            + '.MuiButtonBase-root.MuiIconButton-root:hover,'
            + '.MuiButtonBase-root.MuiButton-outlined:hover,'
            + '.MuiTab-root:hover{background-color:transparent !important;}'
            + '}'
        ].join('');
        document.head.appendChild(style);
    }

    // 客户端启动时会清掉注入的 <style>（踩过：按钮样式整批失效），这里守着
    function guardStyle() {
        ensureStyle();
        if (typeof MutationObserver !== 'function' || !document.head) { return; }
        new MutationObserver(function () { ensureStyle(); }).observe(document.head, { childList: true });
    }

    // ── 清掉触屏上"粘住"的 :hover ────────────────────────────────────────
    // 实测（移动端 Chrome）：手指点过的按钮在之后再点别处，仍然 matches(':hover') === true。
    // pointer-events 开关、blur()、display/visibility/transform 都清不掉，
    // 只有"把节点从文档里摘下来再放回原位"能清掉（同一个节点、同一个位置，React 不受影响）。
    // 不做的话，切页之后那个按钮会一直保持高亮/变色，看着就是"按钮突然变了个颜色"。
    function clearStuckHover() {
        if (!TOUCH || !document.body) { return; }
        var stuck;
        try { stuck = document.querySelectorAll('.MuiButtonBase-root:hover, .card:hover, a:hover, button:hover, [role="button"]:hover'); } catch (e) { return; }
        for (var i = 0; i < stuck.length; i++) {
            var el = stuck[i];
            if (el.closest && el.closest('input, textarea, select')) { continue; }
            var parent = el.parentNode;
            if (!parent) { continue; }
            var next = el.nextSibling;
            try {
                parent.removeChild(el);
                parent.insertBefore(el, next);
            } catch (e) { /* ignore */ }
        }
    }

    // ── 当前可见的页面元素（Jellyfin 12 用 data-role="page"）──────────────
    function visiblePage() {
        var pages = document.querySelectorAll('[data-role="page"], .page');
        var best = null;
        for (var i = 0; i < pages.length; i++) {
            var page = pages[i];
            if (page.classList.contains('hide') || page.hidden) { continue; }
            var rect = page.getBoundingClientRect();
            if (rect.width < 2 || rect.height < 40) { continue; }
            if (!best || rect.height > best.rect.height) { best = { node: page, rect: rect }; }
        }
        return best;
    }

    function pageHasContent(page) {
        if (!page) { return false; }
        if (page.getBoundingClientRect().height < 60) { return false; }
        return page.querySelectorAll('.card, .detailPage, .detailRibbon, .itemBackdrop, .listItem').length > 0
            || (page.innerText || '').trim().length > 40;
    }

    function removeOverlay() {
        if (pollTimer) { clearTimeout(pollTimer); pollTimer = null; }
        var node = overlay;
        overlay = null;
        overlaySource = null;
        if (!node) { return; }
        node.style.opacity = '0';
        setTimeout(function () {
            if (node.parentNode) { node.parentNode.removeChild(node); }
            clearStuckHover();
            // 新页面这时候已经画完了，重新预拍一张给下一次导航用
            scheduleSnapshot(150);
        }, FADE_MS + 40);
    }

    // ── 冻结帧 ───────────────────────────────────────────────────────────
    // 为什么要提前"预拍"一张：点返回（popstate）时，React 的监听器比我们注册得早，
    // 它可能已经先把旧页面拆了、新页面还空着 —— 这时候现场克隆只能克隆到一张空页面。
    // 所以页面每次安定下来（路由切换完成 / 滚动停住）就预拍一张，导航时直接拿来用。
    var snapshot = null;      // { clone, source, rect, scrollY, takenAt }
    var snapshotTimer = null;

    function clonePage(source) {
        var clone = source.cloneNode(true);

        // 克隆出来的 iframe（首页大图区）会重新发起加载，换成同位置同尺寸的底色块
        var frames = clone.querySelectorAll('iframe');
        for (var i = 0; i < frames.length; i++) {
            var frame = frames[i];
            var box = document.createElement('div');
            box.className = frame.className || '';
            box.setAttribute('style', (frame.getAttribute('style') || '') + ';background:' + BG + ';');
            if (frame.parentNode) { frame.parentNode.replaceChild(box, frame); }
        }

        // cloneNode 不复制滚动位置：按下标一一还原（横向滚动条也要）
        var sourceNodes = source.querySelectorAll('*');
        var cloneNodes = clone.querySelectorAll('*');
        for (var j = 0; j < sourceNodes.length && j < cloneNodes.length; j++) {
            if (sourceNodes[j].scrollTop !== 0) { cloneNodes[j].scrollTop = sourceNodes[j].scrollTop; }
            if (sourceNodes[j].scrollLeft !== 0) { cloneNodes[j].scrollLeft = sourceNodes[j].scrollLeft; }
        }

        clone.classList.remove('hide');
        clone.removeAttribute('hidden');
        return clone;
    }

    function takeSnapshot() {
        var found = visiblePage();
        if (!found || !pageHasContent(found.node)) { return; }
        try {
            snapshot = {
                clone: clonePage(found.node),
                source: found.node,
                rect: { left: found.rect.left, top: found.rect.top, width: found.rect.width, height: found.rect.height },
                scrollY: window.scrollY || 0,
                takenAt: Date.now()
            };
        } catch (e) {
            snapshot = null;
        }
    }

    function scheduleSnapshot(delay) {
        if (snapshotTimer) { clearTimeout(snapshotTimer); }
        snapshotTimer = setTimeout(function () { snapshotTimer = null; takeSnapshot(); }, delay);
    }

    function buildOverlay() {
        var live = visiblePage();
        var clone = null;
        var rect = null;
        var source = null;

        // 有新鲜快照就用快照 —— 两种情况都对：
        //   ① 旧页面还在屏幕上（普通前进导航）：快照就是它；
        //   ② DOM 已经被 React 换掉了（点返回时的常见情况）：快照是"上一页"，
        //      而当前 live 往往是新页面的空壳，现场克隆只会克隆到一片空白。
        if (snapshot && snapshot.clone && Date.now() - snapshot.takenAt < 8000) {
            clone = snapshot.clone;
            source = snapshot.source;
            var base = snapshot.rect;
            // 拍完快照之后用户又滚过页面：按窗口滚动的差值平移
            var shift = (window.scrollY || 0) - snapshot.scrollY;
            rect = { left: base.left, top: base.top - shift, width: base.width, height: base.height };
        } else if (live && pageHasContent(live.node)) {
            // 没有快照（例如加载后立刻点了导航）→ 现场克隆，但必须是"有内容的"页面，
            // 否则宁可不出冻结帧，也不要糊一层空白上去
            clone = clonePage(live.node);
            source = live.node;
            rect = live.rect;
        }

        snapshot = null;
        if (!clone) { return null; }

        var wrap = document.createElement('div');
        wrap.id = 'feature-enhance-freeze';
        wrap.setAttribute('aria-hidden', 'true');
        wrap.style.cssText = 'position:fixed;inset:0;z-index:1000;overflow:hidden;pointer-events:none;'
            + 'background:transparent;opacity:1;transition:opacity ' + FADE_MS + 'ms linear;';

        clone.style.position = 'absolute';
        clone.style.left = rect.left + 'px';
        clone.style.top = rect.top + 'px';
        clone.style.width = rect.width + 'px';
        clone.style.minHeight = rect.height + 'px';
        clone.style.margin = '0';
        clone.style.boxShadow = 'none';

        wrap.appendChild(clone);
        document.body.appendChild(wrap);
        return { wrap: wrap, source: source };
    }

    function startFreeze() {
        removeOverlay();
        ensureStyle();
        try {
            var built = buildOverlay();
            if (!built) { return; }
            overlay = built.wrap;
            overlaySource = built.source;
            overlayStart = performance.now();
            waitForNewPage(overlay, overlaySource);
        } catch (e) {
            overlay = null;
            overlaySource = null;
        }
    }

    // ── 等新页面画出内容再淡出 ───────────────────────────────────────────
    function waitForNewPage(node, source) {
        var deadline = Date.now() + MAX_HOLD_MS;
        var readySince = 0;

        function tick() {
            if (overlay !== node) { return; }
            var found = visiblePage();
            var ready = false;

            if (found && pageHasContent(found.node)) {
                if (found.node !== source) {
                    ready = true;
                } else if (performance.now() - overlayStart > 350) {
                    // 同一个容器被复用（列表 → 另一个列表）：内容和旧的长得像，只能按时间兜底
                    ready = true;
                }
            }

            // 从"第一次看到新内容"开始算静置时间：慢页面也不会一出现就被撤掉覆盖层
            if (ready && !readySince) { readySince = performance.now(); }

            if ((readySince && performance.now() - readySince >= SETTLE_MS) || Date.now() > deadline) {
                // 再等两帧，确保新页面已经真的绘制出来
                requestAnimationFrame(function () {
                    requestAnimationFrame(function () { removeOverlay(); });
                });
                return;
            }
            pollTimer = setTimeout(tick, 30);
        }

        pollTimer = setTimeout(tick, MIN_HOLD_MS);
    }

    // ── 路由变化 ─────────────────────────────────────────────────────────
    // 搜索页只是换了关键词（#/search?query=…）属于原地更新，不拍帧
    function routeKey(hash) {
        var value = hash || '';
        return value.replace(/([?&])query=[^&]*/g, '$1').replace(/[?&]$/, '');
    }

    function onRouteChange() {
        var key = routeKey(location.hash);
        var same = key === lastRoute;
        lastRoute = key;
        if (same) { return; }
        clearStuckHover();      // 切页时先把上一个页面粘住的 hover 清掉
        startFreeze();
        // 万一这次没拍成（没有可用的快照）也要补一张，供下一次导航使用
        scheduleSnapshot(1200);
    }

    // 用户一操作就撤掉冻结帧（绝不让一张静态图挡着操作）
    ['wheel', 'touchstart', 'keydown', 'mousedown'].forEach(function (type) {
        window.addEventListener(type, function () {
            interacted = true;
            if (overlay && performance.now() - overlayStart > 30) { removeOverlay(); }
        }, { capture: true, passive: true });
    });

    // ── 滚动位置恢复：尽早、连续几帧校正、用户一动就停 ───────────────────
    var scrollMemory = {};
    var restoring = false;

    function rememberScroll() {
        if (restoring) { return; }
        var hash = location.hash;
        scrollMemory['y:' + hash] = window.scrollY || 0;
        var strips = document.querySelectorAll('.itemsContainer');
        for (var i = 0; i < strips.length; i++) {
            scrollMemory['x:' + hash + ':' + i] = strips[i].scrollLeft;
        }
    }

    function applyScroll(hash) {
        var y = scrollMemory['y:' + hash];
        if (typeof y === 'number' && y > 0 && Math.abs((window.scrollY || 0) - y) > 2) { window.scrollTo(0, y); }
        var strips = document.querySelectorAll('.itemsContainer');
        for (var i = 0; i < strips.length; i++) {
            var x = scrollMemory['x:' + hash + ':' + i];
            if (typeof x === 'number' && x > 0 && Math.abs(strips[i].scrollLeft - x) > 2) { strips[i].scrollLeft = x; }
        }
    }

    function restoreScroll() {
        var hash = location.hash;
        if (!('y:' + hash in scrollMemory)) { return; }
        restoring = true;
        var frames = 0;
        var started = performance.now();

        // 原生把页面显示出来之后还会自己复位一次，所以前面连续几帧都校正，
        // 之后交给用户（用户一动就不再抢）
        interacted = false;
        (function loop() {
            applyScroll(hash);
            frames++;
            if (frames < 12 && performance.now() - started < 900 && !interacted) {
                requestAnimationFrame(loop);
            } else {
                restoring = false;
            }
        })();
    }

    var scrollPending = null;
    document.addEventListener('scroll', function () {
        if (scrollPending) { return; }
        scrollPending = setTimeout(function () { scrollPending = null; rememberScroll(); }, 250);
        // 滚动停住之后再预拍，快照里的滚动位置才不会过期
        scheduleSnapshot(400);
    }, true);

    window.addEventListener('hashchange', function () {
        rememberScroll();
        onRouteChange();
    });

    window.addEventListener('popstate', function () {
        onRouteChange();
        restoreScroll();
    });

    function boot() {
        lastRoute = routeKey(location.hash);
        guardStyle();

        // 关掉浏览器自己的滚动恢复：我们在 popstate 后连续几帧校正位置，
        // 交给浏览器同时恢复就会互相抢（一个设 0、一个设旧位置）。
        try { if ('scrollRestoration' in history) { history.scrollRestoration = 'manual'; } } catch (e) { /* ignore */ }
        // 首次进入：等首页/列表页画完再预拍
        scheduleSnapshot(1200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }

    console.log('[FeatureEnhance] 页面切换冻结帧 + 滚动恢复已启用');
})();
