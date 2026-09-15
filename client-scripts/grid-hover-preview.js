/*
 * Jellyfin 宫格预览 —— 视频卡片左上角的小眼睛按钮
 * ---------------------------------------------------------------------------
 * 点按钮 → 弹出该视频的整屏宫格大图（服务端 /FeatureEnhance/Grid/{id} 实时生成 + 缓存）。
 * 不再使用悬停/长按触发，移动端也只是一个普通按钮，不影响滑动。
 */
(function () {
    'use strict';

    var CONFIG = { maxWidth: 1600, types: ['Video', 'Movie', 'Episode', 'MusicVideo'] };

    var overlay = null, overlayImg = null, overlayStatus = null, overlayClose = null;
    var overlayLoader = null, dotRow = null, spinner = null;
    var loaderDots = [], dotAnimations = [], spinAnimation = null;

    // 注意：注入的 <style> 元素会被 jellyfin-web 在启动过程中清掉（实测按钮样式完全没生效），
    // 所以这里一律用内联样式。
    function isTouch() {
        return ('ontouchstart' in window) || (navigator.maxTouchPoints > 0);
    }

    function btnStyle() {
        // 触屏上按钮要大一些（≥34px），鼠标端保持小巧
        var size = isTouch() ? 34 : 26;
        return 'position:absolute;top:6px;left:6px;z-index:6;width:' + size + 'px;height:' + size + 'px;min-width:' + size + 'px;'
            + 'display:flex;align-items:center;justify-content:center;padding:0;border:0;border-radius:50%;'
            + 'background:rgba(0,0,0,.6);color:#fff;cursor:pointer;opacity:.8;touch-action:manipulation;'
            + 'box-shadow:0 1px 4px rgba(0,0,0,.45);line-height:1;';
    }
    var OVERLAY_STYLE = 'position:fixed;inset:0;z-index:200000;display:none;align-items:center;justify-content:center;'
        + 'background:rgba(0,0,0,.86);cursor:zoom-out;';
    var IMG_STYLE = 'max-width:94vw;max-height:90vh;border-radius:.4em;box-shadow:0 .6em 2em rgba(0,0,0,.65);background:#111;';
    var STATUS_STYLE = 'position:absolute;top:18px;left:50%;transform:translateX(-50%);z-index:2;'
        + 'display:flex;align-items:center;justify-content:center;width:44px;height:44px;border-radius:50%;'
        + 'background:rgba(0,0,0,.72);box-shadow:0 2px 10px rgba(0,0,0,.55);';
    var CLOSE_STYLE = 'position:absolute;top:18px;right:22px;width:42px;height:42px;border:0;border-radius:50%;'
        + 'background:rgba(255,255,255,.14);color:#fff;font-size:18px;cursor:pointer;line-height:1;';

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

    function apiUrl(path, params) {
        var url = null;
        try {
            if (window.ApiClient && window.ApiClient.getUrl) { url = window.ApiClient.getUrl(path, params); }
        } catch (e) { /* fall through */ }
        if (!url) {
            var query = [];
            Object.keys(params || {}).forEach(function (k) {
                if (params[k] !== undefined && params[k] !== null && params[k] !== '') {
                    query.push(k + '=' + encodeURIComponent(params[k]));
                }
            });
            url = '/' + path.replace(/^\//, '') + (query.length ? '?' + query.join('&') : '');
        }
        // Jellyfin 12 查询参数鉴权区分大小写：必须 ApiKey（api_key 会 401）
        var t = accessToken();
        if (t && url.indexOf('ApiKey=') === -1) {
            url += (url.indexOf('?') === -1 ? '?' : '&') + 'ApiKey=' + encodeURIComponent(t);
        }
        return url;
    }

    function showOverlay(visible) {
        if (!overlay) { return; }
        overlay.style.display = visible ? 'flex' : 'none';
    }

    // 九宫格【始终是竖向】的：横向视频由服务端把每一格顺时针旋转 90° 放进竖格，
    // 竖向视频保持原方向；每格都是完整画面（不裁切）。所以客户端不做任何旋转，
    // 弹层里的 <img> 靠 IMG_STYLE 的 max-width/max-height 等比缩放就够了。
    var LOADER_STYLE = 'display:none;flex-direction:column;align-items:center;gap:14px;';
    var DOT_STYLE = 'width:11px;height:11px;border-radius:50%;background:#eee;opacity:.45;';

    // 状态一律用图标，不放文字：
    //   加载中 = 三个跳动的点；重试中 = 旋转的刷新图标；不可用 = 划掉的眼睛（和卡片上那个小眼睛呼应）
    var SPINNER_SVG = '<svg viewBox="0 0 24 24" width="34" height="34" aria-hidden="true">'
        + '<path fill="none" stroke="#eee" stroke-width="2" stroke-linecap="round" '
        + 'd="M12 4.5a7.5 7.5 0 1 1-7.2 5.4"/><path fill="#eee" d="M12 1.6l3.4 3.4L12 8.4z"/></svg>';
    var UNAVAILABLE_SVG = '<svg viewBox="0 0 24 24" width="26" height="26" aria-hidden="true">'
        + '<path fill="#eee" d="M12 6c-4.4 0-8 3.6-8 6 0 1.2 1 2.7 2.6 3.9l1.5-1.5C6.9 13.5 6 12.4 6 12c0-1.4 2.7-4 6-4 .6 0 1.2.1 1.7.2l1.6-1.6C14.3 6.2 13.2 6 12 6zm6.1 2.6l-1.5 1.5c.8.9 1.4 1.5 1.4 1.9 0 1.4-2.7 4-6 4-.6 0-1.2-.1-1.7-.2l-1.6 1.6c1 .4 2.1.6 3.3.6 4.4 0 8-3.6 8-6 0-1.2-1-2.7-1.9-3.4zM4.3 3.3L3 4.6l2.6 2.6C4 8.3 3 9.9 3 12c0 2.4 3.6 6 9 6 1.5 0 2.9-.3 4.1-.8l2.3 2.3 1.3-1.3L4.3 3.3z"/></svg>';

    // 三个跳动的点：用 Web Animations API 而不是 CSS keyframes —— 注入的 <style> 会被
    // web 客户端清掉（见 README 坑 9），所以弹层里的东西一律走内联样式 + JS 动画。
    function startDots() {
        if (!loaderDots.length) { return; }

        stopDots();
        for (var i = 0; i < loaderDots.length; i++) {
            var dot = loaderDots[i];
            if (typeof dot.animate !== 'function') { continue; } // 老浏览器：静态圆点
            dotAnimations.push(dot.animate(
                [
                    { transform: 'translateY(0)', opacity: 0.35 },
                    { transform: 'translateY(-7px)', opacity: 1 },
                    { transform: 'translateY(0)', opacity: 0.35 }
                ],
                { duration: 900, iterations: Infinity, delay: i * 140, easing: 'ease-in-out' }
            ));
        }
    }

    function stopDots() {
        for (var i = 0; i < dotAnimations.length; i++) {
            try { dotAnimations[i].cancel(); } catch (e) { /* ignore */ }
        }
        dotAnimations = [];
    }

    function showLoader(retrying) {
        overlayLoader.style.display = 'flex';
        dotRow.style.display = retrying ? 'none' : 'flex';
        spinner.style.display = retrying ? 'block' : 'none';
        if (retrying) {
            stopDots();
            startSpin();
        } else {
            stopSpin();
            startDots();
        }
    }

    function hideLoader() {
        stopDots();
        stopSpin();
        if (overlayLoader) { overlayLoader.style.display = 'none'; }
    }

    function startSpin() {
        if (typeof spinner.animate !== 'function') { return; }
        stopSpin();
        spinAnimation = spinner.animate(
            [{ transform: 'rotate(0deg)' }, { transform: 'rotate(360deg)' }],
            { duration: 900, iterations: Infinity, easing: 'linear' });
    }

    function stopSpin() {
        if (spinAnimation) {
            try { spinAnimation.cancel(); } catch (e) { /* ignore */ }
            spinAnimation = null;
        }
    }

    function ensureOverlay() {
        if (overlay && overlay.isConnected) { return; }

        // 弹层可能被 web 客户端整块清掉后重建，这里重置引用，避免点点数组越积越多
        stopDots();
        stopSpin();
        loaderDots = [];

        overlay = document.createElement('div');
        overlay.id = 'jfGridOverlay';
        overlay.style.cssText = OVERLAY_STYLE;

        // 加载指示器：overlay 是 flex 居中容器，所以它天然在正中间
        overlayLoader = document.createElement('div');
        overlayLoader.id = 'jfGridLoader';
        overlayLoader.style.cssText = LOADER_STYLE;
        dotRow = document.createElement('div');
        dotRow.style.cssText = 'display:flex;align-items:center;gap:9px;height:16px;';
        for (var d = 0; d < 3; d++) {
            var dot = document.createElement('span');
            dot.style.cssText = DOT_STYLE;
            dotRow.appendChild(dot);
            loaderDots.push(dot);
        }

        spinner = document.createElement('div');
        spinner.style.cssText = 'display:none;line-height:0;';
        spinner.innerHTML = SPINNER_SVG;

        overlayLoader.appendChild(dotRow);
        overlayLoader.appendChild(spinner);

        // 顶部居中的圆形徽章：只在"不可用"时显示，里面是划掉的眼睛图标（无文字）
        overlayStatus = document.createElement('div');
        overlayStatus.id = 'jfGridStatus';
        overlayStatus.style.cssText = STATUS_STYLE;
        overlayStatus.style.display = 'none';
        overlayStatus.innerHTML = UNAVAILABLE_SVG;

        overlayImg = document.createElement('img');
        overlayImg.alt = '';
        overlayImg.style.cssText = IMG_STYLE;

        overlayClose = document.createElement('button');
        overlayClose.id = 'jfGridClose';
        overlayClose.type = 'button';
        overlayClose.textContent = '✕';
        overlayClose.style.cssText = CLOSE_STYLE;
        overlayClose.addEventListener('click', function (e) { e.stopPropagation(); hide(); }, true);

        overlay.appendChild(overlayLoader);
        overlay.appendChild(overlayStatus);
        overlay.appendChild(overlayImg);
        overlay.appendChild(overlayClose);
        overlay.addEventListener('click', hide);
        document.body.appendChild(overlay);
    }

    function open(card) {
        var id = card && (card.getAttribute('data-id') || card.getAttribute('data-itemid'));
        if (!id) { return; }

        ensureOverlay();
        overlayImg.style.display = 'none';
        overlayStatus.style.display = 'none';
        showLoader(false);
        overlayImg.dataset.attempt = '0';
        var showingFallback = false;

        overlayImg.onload = function () {
            hideLoader();
            overlayImg.style.display = '';
            // 不可用徽章要一直留着，否则用户只看到一张"不是宫格"的图，不知道发生了什么
            overlayStatus.style.display = showingFallback ? '' : 'none';
        };

        // 失败重试两次（服务器重启/生成失败/网络抖动），每次带时间戳绕开浏览器缓存。
        // 全失败才退到原生缩略图，并且**明确标注**——否则用户会以为"宫格图变成了蓝色/别的图"
        // （没有封面的视频，原生缩略图是 Jellyfin 的蓝灰色占位图）。
        overlayImg.onerror = function () {
            var attempt = parseInt(overlayImg.dataset.attempt || '0', 10);
            if (attempt < 2) {
                overlayImg.dataset.attempt = String(attempt + 1);
                overlayStatus.style.display = 'none';
                showLoader(true);
                setTimeout(function () {
                    overlayImg.src = apiUrl('FeatureEnhance/Grid/' + id, { r: Date.now() });
                }, 1500 * (attempt + 1));
                return;
            }

            showingFallback = true;
            hideLoader();
            overlayStatus.style.display = '';
            overlayImg.src = apiUrl('Items/' + id + '/Images/Primary', { maxWidth: CONFIG.maxWidth, quality: 90 });
        };

        overlayImg.src = apiUrl('FeatureEnhance/Grid/' + id);
        showOverlay(true);
    }

    function hide() {
        showOverlay(false);
        hideLoader();
    }

    function decorate() {
        // 只认真正的卡片元素 .card：卡片内部还有一个带 data-id/data-type 的标题链接 <a>，
        // 如果按 [data-id][data-type] 遍历，同一张卡会被加两次按钮（就是你看到的两个眼睛）。
        // 只挑"还没处理过"的卡片（打标记），避免每次 DOM 变动都全量扫描
        var cards = document.querySelectorAll('.card[data-id][data-type]:not([data-jfgrid])');
        var added = 0;
        for (var i = 0; i < cards.length; i++) {
            var card = cards[i];
            card.setAttribute('data-jfgrid', '1');
            var type = card.getAttribute('data-type') || '';
            if (CONFIG.types.indexOf(type) === -1) { continue; }
            if (card.querySelector('.jfGridBtn')) { continue; }

            // 按钮挂到缩略图容器上，这样它就在画面左上角、有留白，而不是跟着标题跑
            var anchor = card.querySelector('.cardScalable') || card;
            try {
                if (window.getComputedStyle(anchor).position === 'static') { anchor.style.position = 'relative'; }
            } catch (e) { /* ignore */ }

            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'jfGridBtn';
            btn.style.cssText = btnStyle();
            btn.title = '宫格预览';
            btn.setAttribute('aria-label', '宫格预览');
            btn.innerHTML = '<svg viewBox="0 0 24 24" width="15" height="15" aria-hidden="true">'
                + '<path fill="currentColor" d="M12 5C7 5 3 9.5 3 12s4 7 9 7 9-4.5 9-7-4-7-9-7zm0 11.5A4.5 4.5 0 1 1 12 7.5a4.5 4.5 0 0 1 0 9zm0-7a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z"/></svg>';

            (function (target) {
                // 只阻止冒泡到卡片（避免触发卡片自身的跳转），但**不能** preventDefault：
                // 在 touchstart 上 preventDefault 会把后续的 click 一起吃掉，移动端就点不动了。
                function block(e) { e.stopPropagation(); }
                btn.addEventListener('mousedown', block, true);
                btn.addEventListener('touchstart', block, { capture: true, passive: true });
                btn.addEventListener('touchend', block, { capture: true, passive: true });
                btn.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    open(target);
                }, true);
            })(card);

            anchor.appendChild(btn);
            added++;
        }
        return added;
    }

    var pending = null, lastAdded = 0;
    function schedule() {
        clearTimeout(pending);
        pending = setTimeout(function () {
            var added = decorate();
            if (added > 0 && added !== lastAdded) {
                lastAdded = added;
            }
        }, 300);
    }

    window.addEventListener('hashchange', schedule);
    document.addEventListener('DOMContentLoaded', schedule);
    if (document.body) {
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    }

    // 滚动位置恢复**不在这里做**：smooth.js 里那套（popstate 后连续 12 帧、用户一动就停）
    // 才是当前实现。这里原来还留着一份更老的版本（200/600/1200/2000ms 四次校正、期间还会屏蔽
    // 记录），两套同时在 popstate 上抢 window.scrollTo，表现是"返回后 2 秒内自己滚一下会被拽
    // 回去"。那份已经删掉，这里也不再需要别的处理。

    window.jfGridDecorate = schedule;
    try { console.log('[FeatureEnhance] 宫格预览按钮已加载（视频卡片左上角）'); } catch (e) { /* ignore */ }
    schedule();
})();
