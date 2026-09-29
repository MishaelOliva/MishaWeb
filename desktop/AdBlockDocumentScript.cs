namespace MishaWeb;

/// <summary>
/// Audited, built-in page protections that must run before site scripts.
/// Remote filter lists are never allowed to inject JavaScript.
/// </summary>
internal static class AdBlockDocumentScript
{
    /// <summary>
    /// Domains the injected runtime treats as YouTube. MainForm's network-layer
    /// domain set is asserted against this list and against the script source so
    /// the document guard and the request guard cannot drift apart.
    /// </summary>
    internal static readonly string[] YouTubeHostMarkers =
    [
        "youtube.com", "youtube-nocookie.com", "youtubekids.com"
    ];

    public const string Source =
        """
        (() => {
            const styleId = '__misha_adblock_style';
            const remoteStyleId = '__misha_adblock_remote_style';
            const controlChannel = '__MISHA_ADBLOCK_CONTROL_CHANNEL__';
            let enabledForDocument = window.__mishaAdBlockEnabled !== false;
            let stopCleanup = () => {};
            let stopRecovery = () => {};
            let stopPristineFrameHook = () => {};
            const bootstrapObservers = [];
            const stopBootstrapObservers = () => {
                for (const observer of bootstrapObservers) observer.disconnect();
                bootstrapObservers.length = 0;
            };
            const isEnabled = () => enabledForDocument;
            const disableForDocument = () => {
                enabledForDocument = false;
                stopBootstrapObservers();
                stopCleanup();
                stopRecovery();
                stopPristineFrameHook();
                document.getElementById(styleId)?.remove();
                document.getElementById(remoteStyleId)?.remove();
            };
            try {
                // The setter is deliberately inert: this property is writable from
                // page script, and a live setter would let any site turn the
                // shield off for itself. The host reaches the runtime through the
                // private control channel instead, and the site-exception policy
                // script is registered before this one so its assignment to false
                // is observed by the enabledForDocument initializer above.
                Object.defineProperty(window, '__mishaAdBlockEnabled', {
                    configurable: false,
                    enumerable: false,
                    get: () => enabledForDocument,
                    set: () => {}
                });
            } catch (_) { }
            window.addEventListener(controlChannel, disableForDocument);

            const isYouTubeHost = value => value === 'youtube.com'
                || value.endsWith('.youtube.com')
                || value === 'youtube-nocookie.com'
                || value.endsWith('.youtube-nocookie.com')
                || value === 'youtubekids.com'
                || value.endsWith('.youtubekids.com');
            const host = location.hostname.toLowerCase();
            const isYouTube = isYouTubeHost(host);
            const isYouTubeWatch = () => host === 'www.youtube.com'
                && location.pathname === '/watch';
            // The player loop re-reads the watch query several times per tick.
            // Cache the parsed form and invalidate it on the exact string the
            // browser exposes, so a single-page navigation can never reuse a
            // stale video id or start offset.
            let watchQueryKey = null;
            let watchQueryCache = null;
            const watchQuery = () => {
                const search = location.search;
                if (search !== watchQueryKey) {
                    watchQueryKey = search;
                    watchQueryCache = new URLSearchParams(search);
                }
                return watchQueryCache;
            };
            const recoveryMaskAttribute = 'data-misha-youtube-recovery';
            let recoveryMaskActive = false;
            let recoveryMaskVerified = false;
            let recoveryMaskTimer = 0;
            const syncRecoveryMask = () => {
                if (!document.documentElement) return;
                if (!recoveryMaskActive) {
                    document.documentElement.removeAttribute?.(recoveryMaskAttribute);
                    return;
                }
                document.documentElement.setAttribute?.(
                    recoveryMaskAttribute,
                    recoveryMaskVerified ? 'verified' : '');
            };
            const clearRecoveryMask = () => {
                recoveryMaskActive = false;
                recoveryMaskVerified = false;
                window.clearTimeout(recoveryMaskTimer);
                recoveryMaskTimer = 0;
                syncRecoveryMask();
            };
            const armRecoveryMask = (verified = false) => {
                if (!isEnabled() || !isYouTubeWatch()) return;
                const becameVerified = verified && !recoveryMaskVerified;
                recoveryMaskActive = true;
                recoveryMaskVerified ||= verified;
                syncRecoveryMask();
                if (becameVerified) {
                    window.clearTimeout(recoveryMaskTimer);
                    recoveryMaskTimer = 0;
                }
                if (recoveryMaskTimer) return;
                // Fail open if YouTube never delivers a follow-up player response.
                recoveryMaskTimer = window.setTimeout(clearRecoveryMask, 15_000);
            };

            // Curated list rules and the audited selectors below cover YouTube.
            // Keep these broad fallback heuristics off its application shell so
            // ordinary watch-page controls cannot be mistaken for ad chrome.
            const selectors = isYouTube ? [] : [
                '[id^="ad-"]', '[id*="-ad-"]', '[id$="-ad"]', '[id="ad"]',
                '[class^="ad-"]', '[class*=" ad-"]', '[class$="-ad"]',
                '[id*="advert"]', '[class*="advert"]', '[id*="sponsor"]',
                '[class*="sponsor"]', '[data-ad]', '[data-ad-slot]',
                '[data-ad-client]', '[data-google-query-id]',
                '[aria-label="Advertisement"]', '[aria-label*="advertisement"]',
                '.adsbygoogle', '.advertisement', '.advert', '.ad-banner',
                '.ad-container', '.ad-wrapper', '.ad-unit', '.popunder',
                '.popup-ad', '.ad-overlay', '.sponsor-ad',
                'iframe[src*="doubleclick"]', 'iframe[src*="googlesyndication"]',
                'iframe[src*="adservice"]', 'iframe[src*="adsrvr"]'
            ];

            if (isYouTube) {
                // Ad placements collapse the single unit that hosts them. Hiding a
                // whole feed shelf or search row would take real videos with it,
                // so the :has() rules are scoped to the individual promoted item
                // and the shared ad slot is hidden on its own everywhere else.
                selectors.push(
                    '#masthead-ad', '#panels ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]',
                    '.video-ads', '.ytp-ad-module', '.ytp-ad-overlay-container', '.ytp-ad-progress-list',
                    'ytd-ad-slot-renderer', 'ytd-display-ad-renderer', 'ytd-promoted-sparkles-web-renderer',
                    'ytd-in-feed-ad-layout-renderer', 'ytd-search-pyv-renderer',
                    'ytd-banner-promo-renderer', 'ytd-statement-banner-renderer',
                    'ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer)',
                    'ytd-compact-promoted-item-renderer', 'ytd-promoted-sparkles-text-search-renderer',
                    'ytd-video-masthead-ad-v15-renderer', 'ytd-player-legacy-desktop-watch-ads-renderer',
                    'ytd-reel-item-renderer:has(ad-slot-renderer)',
                    'ytm-companion-ad-renderer', 'ytm-companion-slot', 'ytm-promoted-sparkles-web-renderer',
                    'ytm-shorts-lockup-view-model:has(ad-slot-renderer)',
                    'ytm-rich-item-renderer:has(ad-slot-renderer)', 'ad-slot-renderer',
                    '.ytp-ad-player-overlay', '.ytp-ad-player-overlay-layout',
                    '.ytp-ad-player-overlay-instream-info', '.ytp-ad-text',
                    '.ytp-ad-preview-container', '.ytp-ad-action-interstitial',
                    '.ytp-ad-overlay-slot', '.ytp-ad-image-overlay', '.ytp-ad-overlay-text',
                    'ytm-ad-overlay', 'ytm-player-legacy-desktop-watch-ads-renderer',
                    '.ytp-paid-content-overlay', 'ytd-merchandise-shelf-renderer',
                    '.ytp-ad-player-overlay-flyout-cta', '.ytp-ad-survey',
                    'ytd-ad-break-renderer', '.ytp-suggested-action'
                );
            }

            const installStyle = () => {
                if (!isEnabled()) {
                    stopBootstrapObservers();
                    return;
                }
                if (!document.documentElement || document.getElementById(styleId)) return;
                const style = document.createElement('style');
                style.id = styleId;
                style.textContent = selectors.join(',')
                    + '{display:none !important; visibility:hidden !important; min-height:0 !important;}'
                    + '#cinematics canvas{will-change:transform; transform:translateZ(0);}'
                    + 'tp-yt-paper-dialog:has(ytd-enforcement-message-view-model), '
                    + 'tp-yt-paper-dialog:has(yt-enforcement-message-view-model), '
                    + 'tp-yt-paper-dialog:has(#enforcement-message)'
                    + '{display:none !important; visibility:hidden !important; pointer-events:none !important;}'
                    + `html[${recoveryMaskAttribute}] ytd-enforcement-message-view-model,`
                    + `html[${recoveryMaskAttribute}] yt-enforcement-message-view-model`
                    + '{display:none !important; visibility:hidden !important; pointer-events:none !important;}'
                    // A generic player error is hidden only after the strict
                    // transport-stall predicate verifies that a bounded retry is
                    // underway. The document-start pre-arm cannot hide real errors.
                    + `html[${recoveryMaskAttribute}="verified"] .ytp-error,`
                    + `html[${recoveryMaskAttribute}="verified"] .ytp-error-content-wrap,`
                    + `html[${recoveryMaskAttribute}="verified"] .ytp-error-content,`
                    + `html[${recoveryMaskAttribute}="verified"] `
                    + 'yt-playability-error-supported-renderers#error-screen'
                    + '{display:none !important; visibility:hidden !important; pointer-events:none !important;}';
                document.documentElement.appendChild(style);
                if (recoveryMaskActive) {
                    syncRecoveryMask();
                }
            };
            installStyle();
            if (!document.documentElement) {
                const styleBootstrapObserver = new MutationObserver(() => {
                    installStyle();
                    if (document.documentElement || !isEnabled()) {
                        styleBootstrapObserver.disconnect();
                    }
                });
                bootstrapObservers.push(styleBootstrapObserver);
                styleBootstrapObserver.observe(document, { childList: true });
            }
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', installStyle, { once: true });
            }

            if (!isYouTube || !isEnabled()) return;

            // Avoid wrapping and traversing ordinary browse, search, comments,
            // and account responses. Player-bearing endpoints are the only
            // fetch/XHR payloads that need the deeper metadata sanitizer.
            const playerPayloadPath = /\/(?:youtubei\/v1\/(?:player|get_watch)|youtubei\/v1\/reel\/(?:reel_watch_sequence|reel_item_watch)|get_video_info|playlist|watch)(?:[/?]|$)/;
            const requestUrl = request => {
                try {
                    if (typeof request === 'string') return request;
                    if (request && typeof request.url === 'string') return request.url;
                    return request instanceof URL ? request.href : '';
                } catch (_) { return ''; }
            };
            const shouldSanitizePlayerPayload = request => {
                const value = requestUrl(request);
                if (!value || !playerPayloadPath.test(value)) return false;
                try {
                    const target = new URL(value, location.href);
                    return isYouTubeHost(target.hostname.toLowerCase())
                        && playerPayloadPath.test(target.pathname);
                } catch (_) { return false; }
            };

            const adKeys = new Set([
                'adPlacements', 'playerAds', 'adSlots', 'adBreakHeartbeatParams',
                'adBreakParams', 'instreamAdPlayerOverlayRenderer',
                'playerAdParams', 'adLayoutLoggingData', 'paidContentOverlayRenderer',
                'adBreaks', 'adBreak', 'adSlotRenderer', 'adPlacementRenderer',
                'adBreakServiceRenderer', 'instreamVideoAdRenderer',
                'adSafetyReason', 'linearAdSequenceRenderer', 'playerLegacyDesktopWatchAdsRenderer',
                'adBreakRenderer', 'adPlacement', 'videoAdRenderer',
                'instreamAdVideoRenderer', 'adEngagementPanelContent',
                'adsEngagementPanelContentRenderer'
            ]);
            const isAdEntry = value => !!value && typeof value === 'object' && (
                value.adSlotRenderer
                || value.richItemRenderer?.content?.adSlotRenderer
                || value.command?.reelWatchEndpoint?.adClientParams?.isAd
                || value.adPlacementRenderer
                || value.adBreakServiceRenderer
                || value.instreamVideoAdRenderer
            );
            const sanitizedObjects = new WeakSet();

            const sanitize = value => {
                if (!isEnabled()
                    || !value
                    || typeof value !== 'object'
                    || sanitizedObjects.has(value)) return value;
                const pending = [value];
                const seen = new WeakSet();
                let visited = 0;
                while (pending.length && visited++ < 25000) {
                    const current = pending.pop();
                    if (!current
                        || typeof current !== 'object'
                        || seen.has(current)
                        || sanitizedObjects.has(current)) continue;
                    seen.add(current);

                    if (Array.isArray(current)) {
                        for (let index = current.length - 1; index >= 0; index--) {
                            const item = current[index];
                            if (isAdEntry(item)) {
                                try { current.splice(index, 1); } catch (_) { }
                            } else if (item && typeof item === 'object') {
                                pending.push(item);
                            }
                        }
                        continue;
                    }

                    for (const key of Object.keys(current)) {
                        if (adKeys.has(key)) {
                            try { delete current[key]; }
                            catch (_) { try { current[key] = undefined; } catch (_) { } }
                            continue;
                        }
                        const child = current[key];
                        if (child && typeof child === 'object') pending.push(child);
                    }
                }
                if (pending.length === 0) sanitizedObjects.add(value);
                return value;
            };

            const isValidYouTubeVideoId = value => /^[A-Za-z0-9_-]{11}$/.test(value || '');
            let serverRecoveryExhausted = false;

            const repairPlayerResponse = obj => {
                if (!isEnabled() || !obj || typeof obj !== 'object') return obj;
                try {
                    const playability = obj.playabilityStatus;
                    if (playability && typeof playability === 'object') {
                        const hasStreams = Boolean(
                            (Array.isArray(obj.streamingData?.adaptiveFormats) && obj.streamingData.adaptiveFormats.length > 0)
                            || (Array.isArray(obj.streamingData?.formats) && obj.streamingData.formats.length > 0)
                        );
                        const isEnforcement = playability.status === 'ERROR'
                            || playability.status === 'UNPLAYABLE'
                            || Boolean(playability.errorScreen?.enforcementMessageViewModel)
                            || Boolean(playability.errorScreen?.playerErrorMessageRenderer);

                        if (!obj.videoDetails && isYouTubeWatch()) {
                            const vId = watchQuery().get('v') || '';
                            if (isValidYouTubeVideoId(vId)) {
                                obj.videoDetails = { videoId: vId };
                            }
                        }

                        if (serverRecoveryExhausted && hasStreams && (isEnforcement || playability.status !== 'OK')) {
                            playability.status = 'OK';
                            try { delete playability.errorScreen; } catch (_) { playability.errorScreen = undefined; }
                            try { delete playability.reason; } catch (_) { playability.reason = undefined; }
                            try { delete playability.subreason; } catch (_) { playability.subreason = undefined; }
                            try { delete playability.messages; } catch (_) { playability.messages = undefined; }
                        }
                    }
                } catch (_) { }
                return obj;
            };

            const sanitizePayload = value => {
                if (!isEnabled() || typeof value !== 'string') return value;
                try {
                    const parsed = JSON.parse(value);
                    const sanitized = sanitize(parsed);
                    repairPlayerResponse(sanitized);
                    return JSON.stringify(sanitized);
                } catch (_) { return value; }
            };

            // Current uBO quick fixes disable YouTube's delayed network-machine
            // detector before it can replace an already-playable response with an
            // enforcement error. Trap only the two audited flags; do not patch
            // Promise/Map prototypes or unrelated experiment configuration.
            const patchedYtcfgObjects = new WeakSet();
            const patchedYtcfgData = new WeakSet();
            const forceFalseFlag = (flags, name) => {
                if (!isEnabled()) return;
                if (!flags || typeof flags !== 'object') return;
                try {
                    const descriptor = Object.getOwnPropertyDescriptor(flags, name);
                    if (descriptor && !descriptor.configurable) {
                        flags[name] = false;
                        return;
                    }
                    Object.defineProperty(flags, name, {
                        configurable: true,
                        enumerable: descriptor?.enumerable ?? true,
                        get: () => false,
                        set: () => {}
                    });
                } catch (_) {
                    try { flags[name] = false; } catch (_) { }
                }
            };
            const forceTrueFlag = (flags, name) => {
                if (!isEnabled()) return;
                if (!flags || typeof flags !== 'object') return;
                try {
                    const descriptor = Object.getOwnPropertyDescriptor(flags, name);
                    if (descriptor && !descriptor.configurable) {
                        flags[name] = true;
                        return;
                    }
                    Object.defineProperty(flags, name, {
                        configurable: true,
                        enumerable: descriptor?.enumerable ?? true,
                        get: () => true,
                        set: () => {}
                    });
                } catch (_) {
                    try { flags[name] = true; } catch (_) { }
                }
            };
            const patchExperimentFlags = flags => {
                // These were the only mutations in this file that ignored
                // isEnabled(), so "disable for this site" left the network
                // experiment flags permanently forced off in the document.
                if (!isEnabled()) return;
                forceFalseFlag(flags, 'all_web_enable_network_machine');
                forceFalseFlag(flags, 'all_web_network_machine_raw_request');
            };
            const sanitizePlayerVars = playerVars => {
                if (!playerVars || typeof playerVars !== 'object') return;
                try {
                    for (const key of ['embedded_player_response', 'raw_player_response', 'player_response']) {
                        if (typeof playerVars[key] === 'string') {
                            playerVars[key] = sanitizePayload(playerVars[key]);
                        } else if (typeof playerVars[key] === 'object') {
                            playerVars[key] = sanitize(playerVars[key]);
                        }
                    }
                } catch (_) { }
            };
            const patchYtcfgData = data => {
                if (!data || typeof data !== 'object') return;
                try {
                    if (data.PLAYER_VARS) sanitizePlayerVars(data.PLAYER_VARS);
                    let storedFlags = data.EXPERIMENT_FLAGS;
                    const descriptor = Object.getOwnPropertyDescriptor(data, 'EXPERIMENT_FLAGS');
                    if (!descriptor || descriptor.configurable) {
                        Object.defineProperty(data, 'EXPERIMENT_FLAGS', {
                            configurable: true,
                            enumerable: descriptor ? descriptor.enumerable : true,
                            get: () => storedFlags,
                            set: value => {
                                storedFlags = value;
                                patchExperimentFlags(value);
                            }
                        });
                    }
                    if (storedFlags) patchExperimentFlags(storedFlags);
                } catch (_) {
                    if (data?.EXPERIMENT_FLAGS) patchExperimentFlags(data.EXPERIMENT_FLAGS);
                }
            };
            const patchYtcfg = config => {
                if (!config || typeof config !== 'object' || patchedYtcfgObjects.has(config)) return;
                patchedYtcfgObjects.add(config);
                try {
                    let storedData = config.data_;
                    const descriptor = Object.getOwnPropertyDescriptor(config, 'data_');
                    if (!descriptor || descriptor.configurable) {
                        Object.defineProperty(config, 'data_', {
                            configurable: true,
                            enumerable: descriptor?.enumerable ?? true,
                            get: () => storedData,
                            set: value => {
                                storedData = value;
                                patchYtcfgData(value);
                            }
                        });
                    }
                    if (storedData) patchYtcfgData(storedData);
                    wrapYtcfgFn(config, 'set');
                    wrapYtcfgFn(config, 'd');
                } catch (_) { if (config.data_) patchYtcfgData(config.data_); }
            };
            const wrapYtcfgFn = (config, fnName) => {
                try {
                    const original = config?.[fnName];
                    if (typeof original !== 'function' || original.__mishaHooked) return;
                    const hooked = function(...args) {
                        if (args[0] && typeof args[0] === 'object') {
                            if (args[0].EXPERIMENT_FLAGS && typeof args[0].EXPERIMENT_FLAGS === 'object') {
                                patchExperimentFlags(args[0].EXPERIMENT_FLAGS);
                            }
                            if (args[0].PLAYER_VARS) sanitizePlayerVars(args[0].PLAYER_VARS);
                        } else if (typeof args[0] === 'string' && args[1] && typeof args[1] === 'object') {
                            if (args[0] === 'EXPERIMENT_FLAGS') patchExperimentFlags(args[1]);
                            if (args[0] === 'PLAYER_VARS') sanitizePlayerVars(args[1]);
                        }
                        const result = original.apply(this, args);
                        if (config.data_?.PLAYER_VARS) sanitizePlayerVars(config.data_.PLAYER_VARS);
                        if (config.data_?.EXPERIMENT_FLAGS) patchExperimentFlags(config.data_.EXPERIMENT_FLAGS);
                        return result;
                    };
                    hooked.__mishaHooked = true;
                    config[fnName] = hooked;
                } catch (_) { }
            };
            try {
                let storedYtcfg = window.ytcfg;
                const descriptor = Object.getOwnPropertyDescriptor(window, 'ytcfg');
                if (!descriptor || descriptor.configurable) {
                    Object.defineProperty(window, 'ytcfg', {
                        configurable: true,
                        enumerable: descriptor?.enumerable ?? true,
                        get: () => storedYtcfg,
                        set: value => {
                            storedYtcfg = value;
                            patchYtcfg(value);
                        }
                    });
                }
                patchYtcfg(storedYtcfg);
            } catch (_) { patchYtcfg(window.ytcfg); }

            const trapYtPlayer = () => {
                try {
                    let storedYtPlayer = window.ytplayer;
                    const patchYtPlayerObj = obj => {
                        if (!obj || typeof obj !== 'object') return;
                        try {
                            let cfg = obj.config;
                            const patchConfig = c => {
                                if (!c || typeof c !== 'object') return;
                                try {
                                    let args = c.args;
                                    const patchArgs = a => {
                                        if (!a || typeof a !== 'object') return;
                                        for (const key of ['player_response', 'raw_player_response', 'embedded_player_response']) {
                                            if (typeof a[key] === 'string' && typeof sanitizePayload === 'function') {
                                                a[key] = sanitizePayload(a[key]);
                                            } else if (typeof a[key] === 'object' && typeof sanitize === 'function') {
                                                a[key] = sanitize(a[key]);
                                            }
                                        }
                                    };
                                    const desc = Object.getOwnPropertyDescriptor(c, 'args');
                                    if (!desc || desc.configurable) {
                                        Object.defineProperty(c, 'args', {
                                            configurable: true,
                                            enumerable: desc?.enumerable ?? true,
                                            get: () => args,
                                            set: v => { args = v; patchArgs(v); }
                                        });
                                    }
                                    patchArgs(args);
                                } catch (_) { }
                            };
                            const cfgDesc = Object.getOwnPropertyDescriptor(obj, 'config');
                            if (!cfgDesc || cfgDesc.configurable) {
                                Object.defineProperty(obj, 'config', {
                                    configurable: true,
                                    enumerable: cfgDesc?.enumerable ?? true,
                                    get: () => cfg,
                                    set: v => { cfg = v; patchConfig(v); }
                                });
                            }
                            patchConfig(cfg);
                        } catch (_) { }
                    };
                    const desc = Object.getOwnPropertyDescriptor(window, 'ytplayer');
                    if (!desc || desc.configurable) {
                        Object.defineProperty(window, 'ytplayer', {
                            configurable: true,
                            enumerable: desc?.enumerable ?? true,
                            get: () => storedYtPlayer,
                            set: v => { storedYtPlayer = v; patchYtPlayerObj(v); }
                        });
                    }
                    patchYtPlayerObj(storedYtPlayer);
                } catch (_) { }
            };
            trapYtPlayer();

            const trapPopupConfig = yt => {
                if (!yt || typeof yt !== 'object') return;
                try {
                    let config = yt.config_;
                    const patchConfig = c => {
                        if (!c || typeof c !== 'object') return;
                        try {
                            let openPopup = c.openPopupConfig;
                            const patchOpenPopup = op => {
                                if (!op || typeof op !== 'object') return;
                                try {
                                    let supported = op.supportedPopups;
                                    const patchSupported = sp => {
                                        if (!sp || typeof sp !== 'object') return;
                                        try { forceFalseFlag(sp, 'adBlockMessageViewModel'); }
                                        catch (_) { }
                                    };
                                    const spDesc = Object.getOwnPropertyDescriptor(op, 'supportedPopups');
                                    if (!spDesc || spDesc.configurable) {
                                        Object.defineProperty(op, 'supportedPopups', {
                                            configurable: true,
                                            enumerable: spDesc?.enumerable ?? true,
                                            get: () => supported,
                                            set: v => { supported = v; patchSupported(v); }
                                        });
                                    }
                                    patchSupported(supported);
                                } catch (_) { }
                            };
                            const opDesc = Object.getOwnPropertyDescriptor(c, 'openPopupConfig');
                            if (!opDesc || opDesc.configurable) {
                                Object.defineProperty(c, 'openPopupConfig', {
                                    configurable: true,
                                    enumerable: opDesc?.enumerable ?? true,
                                    get: () => openPopup,
                                    set: v => { openPopup = v; patchOpenPopup(v); }
                                });
                            }
                            patchOpenPopup(openPopup);
                        } catch (_) { }
                    };
                    const configDesc = Object.getOwnPropertyDescriptor(yt, 'config_');
                    if (!configDesc || configDesc.configurable) {
                        Object.defineProperty(yt, 'config_', {
                            configurable: true,
                            enumerable: configDesc?.enumerable ?? true,
                            get: () => config,
                            set: v => { config = v; patchConfig(v); }
                        });
                    }
                    patchConfig(config);
                } catch (_) { }
            };
            try {
                let storedYt = window.yt;
                const ytDesc = Object.getOwnPropertyDescriptor(window, 'yt');
                if (!ytDesc || ytDesc.configurable) {
                    Object.defineProperty(window, 'yt', {
                        configurable: true,
                        enumerable: ytDesc?.enumerable ?? true,
                        get: () => storedYt,
                        set: v => { storedYt = v; trapPopupConfig(v); }
                    });
                }
                trapPopupConfig(storedYt);
            } catch (_) { trapPopupConfig(window.yt); }

            const getRecoveryContract = playerResponse => {
                const videoId = playerResponse?.videoDetails?.videoId
                    || watchQuery().get('v')
                    || '';
                const status = playerResponse?.playabilityStatus;
                const errorScreen = status?.errorScreen;
                const captcha = errorScreen?.playerErrorMessageRenderer?.playerCaptchaViewModel;
                const description = JSON.stringify(
                    errorScreen?.playerErrorMessageRenderer?.subreason?.runs
                    || errorScreen?.playerInterstitialRenderer?.content
                        ?.interstitialViewModel?.description?.commandRuns
                    || []);
                const exactContractError = status?.status === 'UNPLAYABLE'
                    && !captcha
                    && description.includes('WEB_PAGE_TYPE_UNKNOWN')
                    && description.includes('https://support.google.com/youtube/answer/3037019');
                const enforcement = errorScreen?.enforcementMessageViewModel;
                const enforcementCommands = enforcement?.primaryButton?.onTap
                    ?.parallelCommand?.commands;
                const isExplicitEnforcement = status?.status === 'ERROR'
                    && enforcement
                    && enforcement.isVisible !== false
                    && (enforcementCommands?.some?.(command =>
                        command?.innertubeCommand?.openAdAllowlistInstructionCommand)
                        || /ad[ -]?block/i.test(enforcement.title?.content || ''));
                const isGenericEnforcement = status?.status === 'ERROR'
                    && !captcha
                    && (!status?.reason || Boolean(enforcement));
                const exactAdBlockEnforcement = isExplicitEnforcement || isGenericEnforcement;
                return {
                    videoId,
                    status,
                    exactContractError,
                    exactAdBlockEnforcement: !!exactAdBlockEnforcement
                };
            };
            let pendingRecoverySignal = false;
            let queuePlayerRecovery = () => { pendingRecoverySignal = true; };
            const inspectPlayerResponse = playerResponse => {
                const contract = getRecoveryContract(playerResponse);
                if (!contract.videoId) return;
                if (contract.exactAdBlockEnforcement) {
                    if (serverRecoveryExhausted) return;
                    // Verify the exact model before hiding any YouTube error surface.
                    armRecoveryMask(true);
                    queuePlayerRecovery();
                } else if (contract.exactContractError) {
                    queuePlayerRecovery();
                }
            };

            const trapGlobal = name => {
                try {
                    let stored = typeof window[name] === 'string'
                        ? sanitizePayload(window[name])
                        : sanitize(window[name]);
                    inspectPlayerResponse(stored);
                    if (stored && typeof stored === 'object') repairPlayerResponse(stored);
                    Object.defineProperty(window, name, {
                        configurable: true,
                        enumerable: true,
                        get: () => stored,
                        set: value => {
                            stored = typeof value === 'string'
                                ? sanitizePayload(value)
                                : sanitize(value);
                            inspectPlayerResponse(stored);
                            if (stored && typeof stored === 'object') repairPlayerResponse(stored);
                        }
                    });
                } catch (_) { }
            };
            trapGlobal('ytInitialPlayerResponse');
            trapGlobal('playerResponse');

            try {
                let storedInitialData = typeof window.ytInitialData === 'string'
                    ? sanitizePayload(window.ytInitialData)
                    : sanitize(window.ytInitialData);
                Object.defineProperty(window, 'ytInitialData', {
                    configurable: true,
                    enumerable: true,
                    get: () => storedInitialData,
                    set: value => {
                        storedInitialData = typeof value === 'string'
                            ? sanitizePayload(value)
                            : sanitize(value);
                    }
                });
            } catch (_) { }

            const recoveryMarkers = ['channel', 'lactmilli'];
            let recoveryVideoId = '';
            let recoveryAttempt = 0;
            let seenRecoveryResponses = new WeakSet();
            let originalClientUserAgent = null;
            let recoveryClient = null;
            let recoveryClientHadScreen = false;
            let originalClientScreen;
            let activeRecoveryMarker = '';
            let activeRecoveryMarkerOwner = '';
            let transportRecoveryVideoId = '';
            let transportRecoveryAttempt = 0;
            let transportRecoveryResponse = null;
            let transportRecoveryAttemptAt = 0;
            let transportRecoveryStartSeconds = 0;
            let transportRecoveryBaselineTime = Number.NaN;
            let transportRecoveryBaselineBufferedEnd = 0;
            let transportRecoveryHasPlayed = false;
            let transportRecoveryTerminal = false;
            let transportRecoveryErrorNode = null;
            let transportRecoveryFastPollUntil = isYouTubeWatch()
                ? performance.now() + 12_000
                : 0;
            const isPremium = () => window.ytInitialData?.topbar?.desktopTopbarRenderer
                ?.logo?.topbarLogoRenderer?.iconImage?.iconType === 'YOUTUBE_PREMIUM_LOGO';
            const parseStartSeconds = value => {
                const normalized = String(value || '').trim().toLowerCase();
                if (!normalized) return 0;
                if (/^\d+(?:\.\d+)?s?$/.test(normalized)) {
                    const numeric = Number.parseFloat(normalized);
                    return Number.isFinite(numeric) && numeric >= 0 ? numeric : 0;
                }
                const match = /^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$/.exec(normalized);
                if (!match || !match.slice(1).some(Boolean)) return 0;
                const seconds = Number(match[1] || 0) * 3600
                    + Number(match[2] || 0) * 60
                    + Number(match[3] || 0);
                return Number.isFinite(seconds) && seconds >= 0 ? seconds : 0;
            };
            const getRequestedStartSeconds = playerResponse => {
                const query = watchQuery();
                const requested = query.get('t') ?? query.get('start');
                if (requested !== null) return parseStartSeconds(requested);
                const configured = Number(
                    playerResponse?.playerConfig?.playbackStartConfig?.startSeconds);
                if (Number.isFinite(configured) && configured > 0) return configured;
                return 0;
            };
            const resetTransportRecovery = (videoId = '', playerResponse = null) => {
                if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                if (transportRecoveryAttempt > 0 && recoveryMaskVerified) clearRecoveryMask();
                transportRecoveryVideoId = videoId;
                transportRecoveryAttempt = 0;
                transportRecoveryResponse = playerResponse;
                transportRecoveryAttemptAt = 0;
                transportRecoveryStartSeconds = getRequestedStartSeconds(playerResponse);
                transportRecoveryBaselineTime = Number.NaN;
                transportRecoveryBaselineBufferedEnd = 0;
                transportRecoveryHasPlayed = false;
                transportRecoveryTerminal = false;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = isYouTubeWatch()
                    ? performance.now() + 12_000
                    : 0;
            };
            const restoreRecoveryClient = () => {
                if (!recoveryClient) return;
                try {
                    if (originalClientUserAgent !== null) {
                        recoveryClient.userAgent = originalClientUserAgent;
                    }
                    if (recoveryClientHadScreen) {
                        recoveryClient.clientScreen = originalClientScreen;
                    } else {
                        delete recoveryClient.clientScreen;
                    }
                } catch (_) { }
            };
            const setRecoveryMarker = (marker, owner = 'server') => {
                if (!marker) {
                    restoreRecoveryClient();
                    recoveryClient = null;
                    originalClientUserAgent = null;
                    recoveryClientHadScreen = false;
                    originalClientScreen = undefined;
                    activeRecoveryMarker = '';
                    activeRecoveryMarkerOwner = '';
                    return true;
                }
                const client = window.ytcfg?.data_?.INNERTUBE_CONTEXT?.client;
                if (!client) return false;
                if (recoveryClient && recoveryClient !== client) {
                    restoreRecoveryClient();
                    recoveryClient = null;
                    originalClientUserAgent = null;
                }
                if (!recoveryClient) {
                    recoveryClient = client;
                    originalClientUserAgent = String(client.userAgent || '');
                    recoveryClientHadScreen = Object.prototype.hasOwnProperty.call(
                        client,
                        'clientScreen');
                    originalClientScreen = client.clientScreen;
                }
                try {
                    if (recoveryClientHadScreen) client.clientScreen = originalClientScreen;
                    else delete client.clientScreen;
                    const marked = originalClientUserAgent.replace(
                        /(Mozilla\/5\.0 \([^)]+)/,
                        `$1; ${marker}`);
                    client.userAgent = marked === originalClientUserAgent
                        ? `${originalClientUserAgent} ${marker}`.trim()
                        : marked;
                    if (marker === 'channel') client.clientScreen = 'CHANNEL';
                    activeRecoveryMarker = marker;
                    activeRecoveryMarkerOwner = owner;
                    return client.userAgent.includes(marker)
                        && (marker !== 'channel' || client.clientScreen === 'CHANNEL');
                } catch (_) {
                    setRecoveryMarker('');
                    return false;
                }
            };
            stopRecovery = () => {
                clearRecoveryMask();
                setRecoveryMarker('');
                recoveryAttempt = recoveryMarkers.length;
                serverRecoveryExhausted = true;
                resetTransportRecovery();
                transportRecoveryAttempt = recoveryMarkers.length;
                transportRecoveryTerminal = true;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
                pendingRecoverySignal = false;
            };
            const rewritePlayerRequest = (url, bodyText) => {
                const isPlayerTarget = /\/youtubei\/v1\/player(?:\?|$)/.test(url || '')
                    || /\/youtubei\/v1\/(?:reel\/(?:reel_item_watch|reel_watch_sequence)|get_watch)(?:\?|$)/.test(url || '');
                if (!isEnabled()
                    || typeof bodyText !== 'string'
                    || !bodyText.trim()
                    || !isPlayerTarget) return bodyText;
                try {
                    const body = JSON.parse(bodyText);
                    const client = body?.context?.client;
                    if (!body || typeof body !== 'object') return bodyText;
                    let mutated = false;
                    if (client && activeRecoveryMarker === 'channel') {
                        client.clientScreen = 'CHANNEL';
                        mutated = true;
                    }
                    const playback = body.playbackContext?.contentPlaybackContext;
                    if (playback && (activeRecoveryMarker === 'lactmilli' || !playback.lactMilliseconds)) {
                        playback.lactMilliseconds = String(Date.now());
                        mutated = true;
                    }
                    if (client && typeof client.referer === 'string'
                        && activeRecoveryMarker
                        && !client.referer.includes('#reloadxhr')) {
                        client.referer += '#reloadxhr';
                        mutated = true;
                    }
                    return mutated ? JSON.stringify(body) : bodyText;
                } catch (_) { return bodyText; }
            };

            const patchResponse = (response, request) => {
                if (!isEnabled()
                    || !response
                    || response.__mishaAdBlockPatched
                    || (!shouldSanitizePlayerPayload(request)
                        && !shouldSanitizePlayerPayload(response.url))) return response;
                try {
                    const nativeJson = response.json.bind(response);
                    Object.defineProperty(response, 'json', {
                        configurable: true,
                        value: (...args) => nativeJson(...args).then(res => {
                            const sanitized = sanitize(res);
                            repairPlayerResponse(sanitized);
                            inspectPlayerResponse(sanitized);
                            return sanitized;
                        })
                    });
                    const nativeText = response.text.bind(response);
                    Object.defineProperty(response, 'text', {
                        configurable: true,
                        value: (...args) => nativeText(...args).then(text => {
                            const sanitizedText = sanitizePayload(text);
                            try {
                                const parsed = JSON.parse(sanitizedText);
                                inspectPlayerResponse(parsed);
                            } catch (_) { }
                            return sanitizedText;
                        })
                    });
                    const nativeClone = response.clone.bind(response);
                    Object.defineProperty(response, 'clone', {
                        configurable: true,
                        value: (...args) => patchResponse(nativeClone(...args), request)
                    });
                    Object.defineProperty(response, '__mishaAdBlockPatched', { value: true });
                } catch (_) { }
                return response;
            };

            try {
                const nativeFetch = window.fetch;
                window.fetch = new Proxy(nativeFetch, {
                    apply(target, thisArg, args) {
                        try {
                            const firstArg = args[0];
                            const isReq = typeof Request !== 'undefined' && firstArg instanceof Request;
                            const url = isReq ? firstArg.url : requestUrl(firstArg);
                            if (isReq && shouldSanitizePlayerPayload(firstArg)) {
                                return (async () => {
                                    try {
                                        const bodyText = await firstArg.clone().text();
                                        const rewritten = rewritePlayerRequest(url, bodyText);
                                        if (rewritten && rewritten !== bodyText) {
                                            const headers = new Headers(firstArg.headers);
                                            if (!headers.has('Content-Type')) {
                                                headers.set('Content-Type', 'application/json');
                                            }
                                            const init = {
                                                method: firstArg.method,
                                                headers: headers,
                                                mode: firstArg.mode,
                                                credentials: firstArg.credentials,
                                                cache: firstArg.cache,
                                                redirect: firstArg.redirect,
                                                referrer: firstArg.referrer,
                                                integrity: firstArg.integrity,
                                                body: rewritten
                                            };
                                            const newReq = new Request(firstArg.url, init);
                                            const response = await Reflect.apply(target, thisArg, [newReq]);
                                            return patchResponse(response, newReq);
                                        }
                                    } catch (_) { }
                                    const res = await Reflect.apply(target, thisArg, args);
                                    return patchResponse(res, firstArg);
                                })();
                            }
                            const body = args[1]?.body;
                            if (typeof body === 'string') {
                                const rewrittenBody = rewritePlayerRequest(url, body);
                                if (rewrittenBody !== body) {
                                    args[1] = { ...(args[1] || {}), body: rewrittenBody };
                                }
                            }
                        } catch (_) { }
                        const pending = Reflect.apply(target, thisArg, args);
                        return shouldSanitizePlayerPayload(args[0])
                            ? pending.then(response => patchResponse(response, args[0]))
                            : pending;
                    }
                });
            } catch (_) { }

            try {
                const requestUrls = new WeakMap();
                const nativeOpen = XMLHttpRequest.prototype.open;
                XMLHttpRequest.prototype.open = new Proxy(nativeOpen, {
                    apply(target, thisArg, args) {
                        try { requestUrls.set(thisArg, new URL(args[1], location.href).href); }
                        catch (_) { }
                        return Reflect.apply(target, thisArg, args);
                    }
                });
                const responseDescriptor = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'response');
                if (responseDescriptor?.get && responseDescriptor.configurable) {
                    Object.defineProperty(XMLHttpRequest.prototype, 'response', {
                        configurable: true,
                        enumerable: responseDescriptor.enumerable,
                        get() {
                            const value = responseDescriptor.get.call(this);
                            return shouldSanitizePlayerPayload(requestUrls.get(this))
                                ? sanitizePayload(value)
                                : value;
                        }
                    });
                }
                const responseTextDescriptor = Object.getOwnPropertyDescriptor(
                    XMLHttpRequest.prototype,
                    'responseText');
                if (responseTextDescriptor?.get && responseTextDescriptor.configurable) {
                    Object.defineProperty(XMLHttpRequest.prototype, 'responseText', {
                        configurable: true,
                        enumerable: responseTextDescriptor.enumerable,
                        get() {
                            const value = responseTextDescriptor.get.call(this);
                            return shouldSanitizePlayerPayload(requestUrls.get(this))
                                ? sanitizePayload(value)
                                : value;
                        }
                    });
                }
                const nativeSend = XMLHttpRequest.prototype.send;
                XMLHttpRequest.prototype.send = new Proxy(nativeSend, {
                    apply(target, thisArg, args) {
                        try {
                            const url = requestUrls.get(thisArg);
                            args[0] = rewritePlayerRequest(url, args[0]);
                            if (shouldSanitizePlayerPayload(url)) {
                                thisArg.addEventListener(
                                    'load',
                                    () => {
                                        try {
                                            const raw = thisArg.response;
                                            const obj = typeof raw === 'string' ? JSON.parse(raw) : raw;
                                            const res = sanitize(obj);
                                            repairPlayerResponse(res);
                                            inspectPlayerResponse(res);
                                        } catch (_) {
                                            const res = sanitize(thisArg.response);
                                            repairPlayerResponse(res);
                                            inspectPlayerResponse(res);
                                        }
                                    },
                                    { once: true });
                            }
                        } catch (_) { }
                        return Reflect.apply(target, thisArg, args);
                    }
                });
            } catch (_) { }

            // YouTube sometimes reads request functions from a sandboxed
            // about:blank frame. Intercept that iframe-specific read instead of
            // proxying Node.appendChild for every DOM insertion on the page.
            try {
                const contentWindowDescriptor = Object.getOwnPropertyDescriptor(
                    HTMLIFrameElement.prototype,
                    'contentWindow');
                if (contentWindowDescriptor?.get && contentWindowDescriptor.configurable) {
                    const patchedFrameWindows = new WeakSet();
                    const patchFrameWindow = frameWindow => {
                        if (!isEnabled()
                            || !frameWindow
                            || patchedFrameWindows.has(frameWindow)) return frameWindow;
                        try {
                            frameWindow.fetch = window.fetch;
                            frameWindow.Request = window.Request;
                            frameWindow.XMLHttpRequest = window.XMLHttpRequest;
                            patchedFrameWindows.add(frameWindow);
                        } catch (_) { }
                        return frameWindow;
                    };
                    const contentWindowGetter = function() {
                        return patchFrameWindow(contentWindowDescriptor.get.call(this));
                    };
                    Object.defineProperty(HTMLIFrameElement.prototype, 'contentWindow', {
                        ...contentWindowDescriptor,
                        get: contentWindowGetter
                    });
                    const patchLoadedFrame = event => {
                        const frame = event.target;
                        if (frame instanceof HTMLIFrameElement) patchFrameWindow(frame.contentWindow);
                    };
                    document.addEventListener('load', patchLoadedFrame, true);
                    stopPristineFrameHook = () => {
                        document.removeEventListener('load', patchLoadedFrame, true);
                        const currentDescriptor = Object.getOwnPropertyDescriptor(
                            HTMLIFrameElement.prototype,
                            'contentWindow');
                        if (currentDescriptor?.get === contentWindowGetter) {
                            Object.defineProperty(
                                HTMLIFrameElement.prototype,
                                'contentWindow',
                                contentWindowDescriptor);
                        }
                    };
                }
            } catch (_) { }

            const skipSelectors = [
                '.ytp-ad-skip-button', '.ytp-ad-skip-button-modern',
                '.ytp-skip-ad-button', '.ytp-skip-ad-button-modern',
                '.ytp-ad-overlay-close-button',
                'button.ytp-ad-skip-button-modern',
                'button.ytp-ad-skip-button',
                '.ytp-ad-skip-button-container button',
                '.ytp-ad-skip-button-slot > button',
                '.ytp-ad-skip-button-text',
                '[id^="skip-button:"] button'
            ];
            const skipAdSelector = skipSelectors.join(',');
            const clickedSkipButtons = new Set();

            const maybeRecoverServerContract = (player, playerResponse) => {
                if (host !== 'www.youtube.com'
                    || location.pathname !== '/watch'
                    || isPremium()
                    || !playerResponse) return false;

                const contract = getRecoveryContract(playerResponse);
                const { status, exactContractError, exactAdBlockEnforcement } = contract;
                const isEnforcement = exactContractError || exactAdBlockEnforcement;
                const videoId = getMatchingWatchVideoId(playerResponse, isEnforcement);
                if ((exactContractError || exactAdBlockEnforcement) && !videoId) {
                    if (activeRecoveryMarkerOwner === 'server') setRecoveryMarker('');
                    clearRecoveryMask();
                    return false;
                }
                if (videoId && recoveryVideoId !== videoId) {
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    if (recoveryMaskVerified) clearRecoveryMask();
                    recoveryVideoId = videoId;
                    recoveryAttempt = 0;
                    serverRecoveryExhausted = false;
                    seenRecoveryResponses = new WeakSet();
                }

                if (status?.status === 'OK') {
                    // A transport-stall retry starts from an apparently OK player
                    // response. Keep its request marker until real media progress
                    // proves that the replacement request completed successfully.
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    recoveryAttempt = 0;
                    serverRecoveryExhausted = false;
                    seenRecoveryResponses = new WeakSet();
                    return false;
                }
                if (!exactContractError && !exactAdBlockEnforcement) {
                    if (!recoveryMaskVerified) clearRecoveryMask();
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    return false;
                }
                if (!videoId) return false;
                if (recoveryAttempt >= recoveryMarkers.length) {
                    serverRecoveryExhausted = true;
                    if (activeRecoveryMarkerOwner === 'server') {
                        setRecoveryMarker('');
                    }
                    return false;
                }
                if (typeof player?.loadVideoById !== 'function') {
                    clearRecoveryMask();
                    return false;
                }

                const responseCanBeTracked = (typeof playerResponse === 'object'
                    && playerResponse !== null) || typeof playerResponse === 'function';
                if (responseCanBeTracked && seenRecoveryResponses.has(playerResponse)) return false;

                // The account-side enforcement variant succeeds with the current
                // lact-millisecond contract; keep CHANNEL as its bounded fallback.
                const markerIndex = exactContractError || recoveryAttempt === 0 ? 1 : 0;
                const marker = recoveryMarkers[markerIndex];
                if (!setRecoveryMarker(marker)) {
                    clearRecoveryMask();
                    return false;
                }
                armRecoveryMask(true);
                const startSeconds = playerResponse.playerConfig
                    ?.playbackStartConfig?.startSeconds ?? 0;
                try {
                    if (responseCanBeTracked) seenRecoveryResponses.add(playerResponse);
                    recoveryAttempt = exactContractError
                        ? recoveryMarkers.length
                        : recoveryAttempt + 1;
                    serverRecoveryExhausted = recoveryAttempt >= recoveryMarkers.length;
                    player.loadVideoById(videoId, startSeconds);
                    try { player.playVideo(); } catch (_) { }
                    return true;
                } catch (_) {
                    if (responseCanBeTracked) seenRecoveryResponses.delete(playerResponse);
                    recoveryAttempt = exactContractError ? 0 : Math.max(0, recoveryAttempt - 1);
                    serverRecoveryExhausted = false;
                    setRecoveryMarker('');
                    return false;
                }
            };

            // getStatsForNerds() and getPlayerStateObject() each cross into
            // YouTube's Polymer player, and the loop used to call them up to
            // three and two times per tick. Memoize both for a single pass.
            const createPlayerSnapshot = player => {
                const snapshot = { read: false, state: null, stats: null };
                snapshot.refresh = () => {
                    if (snapshot.read) return snapshot;
                    snapshot.read = true;
                    try { snapshot.state = player?.getPlayerStateObject?.(); } catch (_) { }
                    try { snapshot.stats = player?.getStatsForNerds?.(); } catch (_) { }
                    return snapshot;
                };
                return snapshot;
            };

            // YouTube publishes these as display strings ("0.00 s", "0x0").
            // Parsing them numerically keeps the strict stall predicate working
            // if the formatting ever changes, while still requiring the field to
            // be present so a missing value never reads as a zero stall.
            const parseBufferHealthSeconds = value => {
                if (value === null || value === undefined) return null;
                const match = /-?\d+(?:\.\d+)?/.exec(String(value));
                if (!match) return null;
                const parsed = Number(match[0]);
                return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
            };
            const hasUsableResolution = value => {
                const text = String(value ?? '').trim();
                if (!text) return false;
                const match = /^(\d+)\s*[x×]\s*(\d+)$/.exec(text);
                return match ? Number(match[1]) > 0 && Number(match[2]) > 0 : true;
            };
            const hasZeroResolution = value => {
                // A missing field must never read as a zero-sized video, or the
                // stall predicate would fire on any buffering state.
                if (value === null || value === undefined) return false;
                const match = /^(\d+)\s*[x×]\s*(\d+)$/.exec(String(value).trim());
                return match ? Number(match[1]) === 0 && Number(match[2]) === 0 : false;
            };

            const transportRetryPattern = /(?:this content isn['’]?t available|an error occurred)[,.]?\s*try again later/i;
            const textFromRuns = runs => Array.isArray(runs)
                ? runs.map(run => run?.text || '').join(' ')
                : '';
            const getTransportResponseText = playerResponse => {
                const status = playerResponse?.playabilityStatus;
                const renderer = status?.errorScreen?.playerErrorMessageRenderer;
                return [
                    status?.reason,
                    ...(Array.isArray(status?.messages) ? status.messages : []),
                    renderer?.reason?.simpleText,
                    textFromRuns(renderer?.reason?.runs),
                    renderer?.subreason?.simpleText,
                    textFromRuns(renderer?.subreason?.runs)
                ].filter(Boolean).join(' ');
            };
            const hasMatchingTransportError = playerResponse => {
                if (transportRecoveryErrorNode?.isConnected !== false
                    && transportRetryPattern.test(
                        transportRecoveryErrorNode?.textContent || '')) {
                    return true;
                }
                // The response text is free to read. Prefer it so the common
                // enforcement case never pays for a NodeList walk plus the
                // forced style/layout reads the visible check below needs.
                if (transportRetryPattern.test(getTransportResponseText(playerResponse))
                    && (document.querySelector('ytd-watch-flexy[player-unavailable]')
                        || document.querySelector('.ytp-error'))) {
                    return true;
                }
                const candidates = document.querySelectorAll(
                    '.ytp-error, .ytp-error-content-wrap, .ytp-error-content, .ytp-error-message, '
                    + 'yt-playability-error-supported-renderers#error-screen');
                for (const candidate of candidates) {
                    try {
                        const text = candidate.textContent || candidate.innerText || '';
                        if (!transportRetryPattern.test(text)) continue;
                        const style = getComputedStyle(candidate);
                        const bounds = candidate.getBoundingClientRect();
                        const visible = style.display !== 'none'
                            && style.visibility !== 'hidden'
                            && style.opacity !== '0'
                            && bounds.width > 0
                            && bounds.height > 0;
                        if (visible) {
                            transportRecoveryErrorNode = candidate;
                            return true;
                        }
                    } catch (_) { }
                }
                return false;
            };

            const getBufferedEnd = video => {
                try {
                    const ranges = video?.buffered;
                    if (!ranges?.length) return 0;
                    const end = Number(ranges.end(ranges.length - 1));
                    return Number.isFinite(end) && end >= 0 ? end : 0;
                } catch (_) {
                    return 0;
                }
            };

            const getMatchingWatchVideoId = (playerResponse, allowRequestedFallback = false) => {
                const requested = watchQuery().get('v') || '';
                const response = playerResponse?.videoDetails?.videoId
                    || window.ytInitialPlayerResponse?.videoDetails?.videoId
                    || (allowRequestedFallback ? requested : '');
                return isValidYouTubeVideoId(requested)
                    && isValidYouTubeVideoId(response)
                    && requested === response
                    ? response
                    : '';
            };

            const hasActualMediaProgress = (player, playerResponse, progress, video, snapshot) => {
                const videoId = getMatchingWatchVideoId(playerResponse);
                if (!videoId) return false;
                if (transportRecoveryVideoId !== videoId) {
                    resetTransportRecovery(videoId, playerResponse);
                }
                if (!(video instanceof HTMLVideoElement) || video.readyState < 2) {
                    return false;
                }
                const current = Number(video.currentTime || 0);
                const bufferedEnd = getBufferedEnd(video);
                if (!Number.isFinite(transportRecoveryBaselineTime)) {
                    transportRecoveryBaselineTime = current;
                    transportRecoveryBaselineBufferedEnd = bufferedEnd;
                    return false;
                }
                const currentAdvanced = transportRecoveryAttempt > 0
                    && current > transportRecoveryBaselineTime + 0.1;
                const bufferAdvanced = transportRecoveryAttempt > 0
                    && bufferedEnd > transportRecoveryBaselineBufferedEnd + 0.25;
                const observed = (snapshot || createPlayerSnapshot(player)).refresh();
                const state = observed.state;
                const stats = observed.stats;
                const bufferHealth = parseBufferHealthSeconds(stats?.buffer_health_seconds);
                const strictPlayable = transportRecoveryAttempt > 0
                    && playerResponse?.playabilityStatus?.status === 'OK'
                    && state?.isBuffering === false
                    && hasUsableResolution(stats?.resolution)
                    && bufferHealth !== null
                    && bufferHealth > 0
                    && !hasMatchingTransportError(playerResponse)
                    && video.readyState >= 3
                    && (video.paused === false || bufferedEnd > current + 0.05);
                const ready = transportRecoveryAttempt > 0
                    ? currentAdvanced || bufferAdvanced || strictPlayable
                    : current > transportRecoveryBaselineTime + 0.1;
                if (!ready) return false;
                transportRecoveryAttempt = 0;
                transportRecoveryResponse = playerResponse;
                transportRecoveryAttemptAt = 0;
                transportRecoveryBaselineTime = current;
                transportRecoveryBaselineBufferedEnd = bufferedEnd;
                transportRecoveryHasPlayed = true;
                transportRecoveryTerminal = false;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
                if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                clearRecoveryMask();
                return true;
            };

            const finalizeTransportRecovery = () => {
                if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                clearRecoveryMask();
                transportRecoveryAttempt = recoveryMarkers.length;
                transportRecoveryAttemptAt = 0;
                transportRecoveryTerminal = true;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
            };
            const finalizeExpiredTransportRecovery = () => {
                if (transportRecoveryTerminal || transportRecoveryAttempt === 0) return false;
                const elapsed = performance.now() - transportRecoveryAttemptAt;
                if ((transportRecoveryAttempt >= recoveryMarkers.length && elapsed >= 2_000)
                    || elapsed >= 6_000) {
                    finalizeTransportRecovery();
                    return true;
                }
                return false;
            };

            const maybeRecoverTransportStall = (player, playerResponse, progress, video, snapshot) => {
                if (host !== 'www.youtube.com'
                    || location.pathname !== '/watch'
                    || isPremium()
                    || !playerResponse
                    || typeof player?.loadVideoById !== 'function') return false;

                const videoId = getMatchingWatchVideoId(playerResponse);
                if (!videoId) return false;
                if (transportRecoveryVideoId !== videoId) {
                    resetTransportRecovery(videoId, playerResponse);
                }
                if (transportRecoveryTerminal
                    || playerResponse?.videoDetails?.isLive
                    || playerResponse?.videoDetails?.isLiveContent) return false;
                const captcha = playerResponse?.playabilityStatus?.errorScreen
                    ?.playerErrorMessageRenderer?.playerCaptchaViewModel;
                if (captcha) return false;

                const duration = Number(progress?.duration || 0);
                const loaded = Number(progress?.loaded || 0);
                const current = Number(progress?.current || 0);
                const unfinished = duration > 0
                    && (loaded < duration || duration - current > 1);
                const observed = (snapshot || createPlayerSnapshot(player)).refresh();
                const state = observed.state;
                const stats = observed.stats;
                const bufferHealth = parseBufferHealthSeconds(stats?.buffer_health_seconds);
                const stalled = unfinished
                    && state?.isBuffering === true
                    && bufferHealth === 0
                    && hasZeroResolution(stats?.resolution)
                    && hasMatchingTransportError(playerResponse);
                if (!stalled) return false;

                const now = performance.now();
                // Give the request started by the previous attempt a chance to
                // replace the response object. YouTube occasionally mutates the
                // object in place, so permit the alternate marker after 2 seconds.
                if (transportRecoveryAttempt > 0
                    && transportRecoveryResponse === playerResponse
                    && now - transportRecoveryAttemptAt < 2_000) return false;
                if (transportRecoveryAttempt >= recoveryMarkers.length) {
                    return false;
                }

                const marker = recoveryMarkers[transportRecoveryAttempt];
                if (!setRecoveryMarker(marker, 'transport')) return false;
                armRecoveryMask(true);
                transportRecoveryResponse = playerResponse;
                transportRecoveryAttemptAt = now;
                transportRecoveryFastPollUntil = now + 6_000;
                const mediaTime = Number(video?.currentTime);
                const progressTime = Number(progress?.current);
                const observedTime = Number.isFinite(mediaTime) && mediaTime >= 0
                    ? mediaTime
                    : Number.isFinite(progressTime) && progressTime >= 0
                        ? progressTime
                        : 0;
                transportRecoveryStartSeconds = transportRecoveryHasPlayed
                    && Number.isFinite(observedTime)
                    && observedTime >= 0
                    ? observedTime
                    : getRequestedStartSeconds(playerResponse);
                transportRecoveryBaselineTime = observedTime;
                transportRecoveryBaselineBufferedEnd = getBufferedEnd(video);
                try {
                    transportRecoveryAttempt++;
                    player.loadVideoById(videoId, transportRecoveryStartSeconds);
                    return true;
                } catch (_) {
                    if (activeRecoveryMarkerOwner === 'transport') setRecoveryMarker('');
                    clearRecoveryMask();
                    return false;
                }
            };

            let adWasActive = false;
            let userPlaybackRate = 1.0;
            let userWasMuted = false;
            let muteAppliedByBlocker = false;
            // Undoes the mute and 16x speed-up applied while an ad was showing.
            // This has to run on every exit from the ad state, not only when the
            // ad finishes on its own: yt-navigate-start fires when the user picks
            // a suggested video mid-ad, which used to leave the next video
            // playing silently at 16x for the rest of the session.
            const restoreAfterAd = () => {
                if (!adWasActive) return;
                adWasActive = false;
                clickedSkipButtons.clear();
                muteAppliedByBlocker = false;
                const videos = document.querySelectorAll('video.html5-main-video, video');
                for (const video of videos) {
                    if (!(video instanceof HTMLVideoElement)) continue;
                    try {
                        if (video.playbackRate > 2) video.playbackRate = userPlaybackRate || 1.0;
                    } catch (_) { }
                    try { video.muted = userWasMuted; } catch (_) { }
                }
                const player = observedPlayer || document.getElementById('movie_player');
                try {
                    if (!userWasMuted) player?.unMute?.();
                } catch (_) { }
                try { player?.playVideo?.(); } catch (_) { }
            };
            let userManuallyPaused = false;
            let activeWatchVideoId = '';
            let autoplayAttempts = 0;
            let autoplayCompletedForVideoId = '';
            let lastPlayAttempt = 0;
            let watchPageLoadTime = performance.now();
            let hookedVideo = null;
            let removeVideoHooks = () => {};

            const getActiveVideo = () => {
                const player = document.getElementById?.('movie_player') || document.querySelector?.('.html5-video-player');
                const v = player?.querySelector?.('video.html5-main-video')
                    || player?.querySelector?.('video')
                    || document.querySelector?.('video.html5-main-video, video');
                if (v && v.isConnected && v !== hookedVideo) {
                    hookVideoElement(v);
                }
                return v;
            };

            const triggerInstantPlay = () => {
                if (!isEnabled() || !isYouTubeWatch() || userManuallyPaused) return;
                const currentVideoId = watchQuery().get('v') || '';
                if (!currentVideoId) return;

                if (currentVideoId !== activeWatchVideoId) {
                    activeWatchVideoId = currentVideoId;
                    autoplayAttempts = 0;
                    autoplayCompletedForVideoId = '';
                    userManuallyPaused = false;
                    lastPlayAttempt = 0;
                }

                if (autoplayCompletedForVideoId === currentVideoId) return;

                const player = document.getElementById?.('movie_player') || document.querySelector?.('.html5-video-player');
                if (!player) return;

                const isAd = Boolean(
                    player.classList?.contains?.('ad-showing')
                    || player.classList?.contains?.('ad-interrupting')
                );
                if (isAd) return;

                const video = getActiveVideo();

                let playerState = null;
                try {
                    if (typeof player.getPlayerState === 'function') {
                        playerState = player.getPlayerState();
                    }
                } catch (_) { }

                // YouTube player states:
                // -1: unstarted, 0: ended, 1: playing, 2: paused, 3: buffering, 5: video cued
                if (playerState === 1 || (video && !video.paused && (video.currentTime || 0) > 0)) {
                    autoplayCompletedForVideoId = currentVideoId;
                    return;
                }
                const hasMediaAttached = Boolean(video && (video.src || video.currentSrc) && video.readyState >= 1);
                if (playerState === 3 && hasMediaAttached && video && !video.paused) {
                    // Buffering active stream while playing - do not disrupt
                    return;
                }

                // If video has media attached (readyState >= 1 or src), declare play intent immediately!
                const hasMedia = Boolean(video && (video.readyState >= 1 || video.src || video.currentSrc));
                if (!video || !hasMedia) {
                    if (playerState === -1 || playerState === 5) {
                        const now = performance.now();
                        if (now - lastPlayAttempt >= 300) {
                            lastPlayAttempt = now;
                            try { player.playVideo(); } catch (_) { }
                        }
                    }
                    return;
                }

                if (autoplayAttempts >= 20) return;
                autoplayAttempts++;

                const isPausedOrCued = playerState === -1 || playerState === 2 || playerState === 5;
                const isPausedVideo = Boolean(video.paused);

                if (isPausedOrCued || isPausedVideo) {
                    const now = performance.now();
                    if (now - lastPlayAttempt < 150) return;
                    lastPlayAttempt = now;
                    try {
                        if (typeof player.playVideo === 'function') {
                            player.playVideo();
                        }
                    } catch (_) { }
                    try {
                        if (video.paused) {
                            const playPromise = video.play();
                            if (playPromise && typeof playPromise.catch === 'function') {
                                playPromise.catch(() => {});
                            }
                        }
                    } catch (_) { }
                }
            };

            const cleanPlayerAds = () => {
                if (!isEnabled()) return false;
                let adActivity = false;
                try {
                    const player = document.getElementById('movie_player');
                    const playerResponse = player?.getPlayerResponse?.()
                        || window.ytInitialPlayerResponse;
                    if (maybeRecoverServerContract(player, playerResponse)) return true;
                    if (playerResponse) repairPlayerResponse(playerResponse);

                    // Apply the current uBO SSAP gate only to non-Premium watch pages.
                    const snapshot = createPlayerSnapshot(player);
                    const progress = player?.getProgressState?.();
                    const recoveredVideo = document.querySelector('video.html5-main-video, video');
                    const mediaProgressing = hasActualMediaProgress(
                        player,
                        playerResponse,
                        progress,
                        recoveredVideo,
                        snapshot);
                    finalizeExpiredTransportRecovery();
                    if (!mediaProgressing && maybeRecoverTransportStall(
                        player,
                        playerResponse,
                        progress,
                        recoveredVideo,
                        snapshot)) return true;
                    const serverContract = snapshot.refresh().stats?.debug_info;
                    const unfinished = progress?.duration > 0
                        && (progress.loaded < progress.duration
                            || progress.duration - progress.current > 1);
                    if (host === 'www.youtube.com'
                        && location.pathname === '/watch'
                        && !isPremium()
                        && serverContract?.startsWith?.('SSAP, AD')
                        && unfinished) {
                        player.seekTo?.(progress.duration, true);
                        adActivity = true;
                    }

                    const isClientAd = player?.classList?.contains('ad-showing')
                        || player?.classList?.contains('ad-interrupting');
                    if (isClientAd) {
                        adActivity = true;
                        try { player?.mute?.(); } catch (_) { }
                        const videos = document.querySelectorAll('video.html5-main-video, video');
                        for (const video of videos) {
                            if (!(video instanceof HTMLVideoElement)) continue;
                            if (!adWasActive) {
                                adWasActive = true;
                                userWasMuted = video.muted;
                            }
                            // Suppress the volumechange handler below. Assigning
                            // video.muted fires volumechange asynchronously; by the
                            // time it is delivered the ad-showing class is often
                            // already gone, so the handler recorded our own mute as
                            // a user preference and the video stayed silent after
                            // the ad for the rest of the session.
                            muteAppliedByBlocker = true;
                            try { video.muted = true; } catch (_) { }
                            try {
                                if (video.playbackRate < 16) {
                                    video.playbackRate = 16;
                                }
                            } catch (_) { }
                        }
                        // One combined lookup beats nine independent selector
                        // passes, and each distinct control is clicked once per
                        // ad instead of once per poll.
                        for (const button of document.querySelectorAll(skipAdSelector)) {
                            if (!(button instanceof HTMLElement)
                                || clickedSkipButtons.has(button)) continue;
                            clickedSkipButtons.add(button);
                            try { button.click(); } catch (_) { }
                        }
                        try { player?.skipAd?.(); } catch (_) { }
                    } else {
                        restoreAfterAd();
                    }

                    // Query the cheap tag/id selectors. The paper-dialog owner is
                    // resolved with closest() below, which avoids running three
                    // document-wide :has() compounds on every poll.
                    const enforcement = document.querySelector(
                        'ytd-enforcement-message-view-model, yt-enforcement-message-view-model, '
                        + '#enforcement-message');
                    if (enforcement) {
                        adActivity = true;
                        const owner = enforcement.closest?.(
                            'tp-yt-paper-dialog, yt-playability-error-supported-renderers#error-screen');
                        (owner || enforcement).remove?.();
                        document.querySelector('tp-yt-iron-overlay-backdrop')?.remove();
                        player?.classList?.remove?.('ytp-transparent');
                        document.querySelector('ytd-watch-flexy[player-unavailable]')
                            ?.removeAttribute('player-unavailable');
                        player?.playVideo?.();
                    }

                    const playbackReady = playerResponse?.playabilityStatus?.status === 'OK'
                        && recoveredVideo instanceof HTMLVideoElement
                        && recoveredVideo.readyState > 0;
                    if (playbackReady) {
                        if (!recoveryMaskVerified || mediaProgressing) {
                            clearRecoveryMask();
                        }
                    }
                    const popupConfig = window.yt?.config_?.openPopupConfig?.supportedPopups;
                    if (popupConfig && popupConfig.adBlockMessageViewModel !== false) {
                        popupConfig.adBlockMessageViewModel = false;
                        adActivity = true;
                    }
                    if (isYouTubeWatch()) {
                        const currentVideoId = watchQuery().get('v') || '';
                        if (currentVideoId && currentVideoId !== activeWatchVideoId) {
                            activeWatchVideoId = currentVideoId;
                            autoplayAttempts = 0;
                            autoplayCompletedForVideoId = '';
                            userManuallyPaused = false;
                            lastPlayAttempt = 0;
                        }
                        if (!autoplayCompletedForVideoId) triggerInstantPlay();
                    }
                } catch (_) { }
                return adActivity;
            };

            let cleanupQueued = false;
            let urgentCleanupQueued = false;
            let cleanupStarted = false;
            let fallbackTimer = 0;
            let cleanupSignalTimer = 0;
            let observedPlayer = null;
            let playerObserver = null;
            let playerBootstrapObserver = null;
            let playerBootstrapTimer = 0;
            const hookVideoElement = video => {
                if (!video || typeof video.addEventListener !== 'function' || typeof video.removeEventListener !== 'function' || video === hookedVideo) return;
                removeVideoHooks();
                hookedVideo = video;
                const onPlay = () => {
                    const currentVideoId = watchQuery().get('v') || '';
                    if (currentVideoId) autoplayCompletedForVideoId = currentVideoId;
                    cleanPlayerAds();
                };
                const onPlaying = () => {
                    const currentVideoId = watchQuery().get('v') || '';
                    if (currentVideoId) autoplayCompletedForVideoId = currentVideoId;
                };
                const onReady = () => {
                    cleanPlayerAds();
                    if (!autoplayCompletedForVideoId && !userManuallyPaused) {
                        autoplayAttempts = 0;
                        triggerInstantPlay();
                    }
                };
                const onRateChange = () => {
                    const player = observedPlayer || document.getElementById('movie_player');
                    if (!player?.classList?.contains('ad-showing')
                        && !player?.classList?.contains('ad-interrupting')
                        && video.playbackRate <= 2) {
                        userPlaybackRate = video.playbackRate;
                    }
                };
                const onVolumeChange = () => {
                    if (muteAppliedByBlocker || adWasActive) return;
                    const player = observedPlayer || document.getElementById('movie_player');
                    if (!player?.classList?.contains('ad-showing')
                        && !player?.classList?.contains('ad-interrupting')) {
                        userWasMuted = video.muted;
                    }
                };
                video.addEventListener('play', onPlay, { passive: true });
                video.addEventListener('playing', onPlaying, { passive: true });
                video.addEventListener('loadedmetadata', onReady, { passive: true });
                video.addEventListener('loadeddata', onReady, { passive: true });
                video.addEventListener('canplay', onReady, { passive: true });
                video.addEventListener('canplaythrough', onReady, { passive: true });
                video.addEventListener('ratechange', onRateChange, { passive: true });
                video.addEventListener('volumechange', onVolumeChange, { passive: true });
                removeVideoHooks = () => {
                    video.removeEventListener('play', onPlay);
                    video.removeEventListener('playing', onPlaying);
                    video.removeEventListener('loadedmetadata', onReady);
                    video.removeEventListener('loadeddata', onReady);
                    video.removeEventListener('canplay', onReady);
                    video.removeEventListener('canplaythrough', onReady);
                    video.removeEventListener('ratechange', onRateChange);
                    video.removeEventListener('volumechange', onVolumeChange);
                    if (hookedVideo === video) hookedVideo = null;
                };
            };
            const cleanupEvents = ['yt-page-data-updated', 'yt-player-updated'];
            let instantPlayTimer = 0;
            const scheduleInstantPlayCheck = () => {
                window.clearTimeout(instantPlayTimer);
                if (!isEnabled() || !isYouTubeWatch() || autoplayCompletedForVideoId || userManuallyPaused) return;
                triggerInstantPlay();
                if (!autoplayCompletedForVideoId) {
                    instantPlayTimer = window.setTimeout(scheduleInstantPlayCheck, 200);
                }
            };
            const scheduleFallback = adActivity => {
                window.clearTimeout(fallbackTimer);
                if (!isEnabled()) return;
                const fastRecoveryPoll = isYouTubeWatch()
                    && performance.now() < transportRecoveryFastPollUntil;
                const delay = document.hidden
                    ? 30_000
                    : (fastRecoveryPoll ? 250 : (adActivity ? 500 : 3_000));
                fallbackTimer = window.setTimeout(() => {
                    const activity = cleanPlayerAds();
                    if (!autoplayCompletedForVideoId) scheduleInstantPlayCheck();
                    observePlayer();
                    scheduleFallback(activity);
                }, delay);
            };
            const queueCleanup = () => {
                if (cleanupQueued || !isEnabled()) return;
                cleanupQueued = true;
                cleanupSignalTimer = window.setTimeout(() => {
                    cleanupQueued = false;
                    const activity = cleanPlayerAds();
                    observePlayer();
                    if (activity) scheduleFallback(true);
                }, document.hidden ? 5_000 : 150);
            };
            // The player class attribute churns many times per second while an
            // ad is on screen, and every mutation used to run a full pass in a
            // microtask. Keep the first pass immediate and floor the rest so a
            // single ad cannot drive hundreds of redundant player round trips.
            const urgentCleanupIntervalMs = 100;
            let lastUrgentCleanupAt = 0;
            let urgentCleanupTimer = 0;
            const runCleanupPass = () => {
                lastUrgentCleanupAt = performance.now();
                const activity = cleanPlayerAds();
                observePlayer();
                if (activity) scheduleFallback(true);
            };
            const queueUrgentCleanup = () => {
                if (urgentCleanupQueued || !isEnabled()) return;
                urgentCleanupQueued = true;
                const elapsed = performance.now() - lastUrgentCleanupAt;
                if (elapsed >= urgentCleanupIntervalMs) {
                    queueMicrotask(() => {
                        urgentCleanupQueued = false;
                        if (!isEnabled()) return;
                        runCleanupPass();
                    });
                    return;
                }
                window.clearTimeout(urgentCleanupTimer);
                urgentCleanupTimer = window.setTimeout(() => {
                    urgentCleanupTimer = 0;
                    urgentCleanupQueued = false;
                    if (!isEnabled()) return;
                    runCleanupPass();
                }, urgentCleanupIntervalMs - elapsed);
            };
            queuePlayerRecovery = () => {
                pendingRecoverySignal = false;
                queueUrgentCleanup();
            };
            if (pendingRecoverySignal) queuePlayerRecovery();
            const stopPlayerBootstrapObservation = () => {
                playerBootstrapObserver?.disconnect();
                playerBootstrapObserver = null;
                window.clearTimeout(playerBootstrapTimer);
                playerBootstrapTimer = 0;
            };
            const observePlayer = () => {
                if (!isEnabled()) {
                    playerObserver?.disconnect();
                    observedPlayer = null;
                    stopPlayerBootstrapObservation();
                    removeVideoHooks();
                    return;
                }
                const player = document.getElementById('movie_player');
                hookVideoElement(document.querySelector('video.html5-main-video, video'));
                if (player === observedPlayer) return;
                playerObserver?.disconnect();
                observedPlayer = player;
                if (player) {
                    stopPlayerBootstrapObservation();
                    playerObserver?.observe(player, {
                        attributes: true,
                        attributeFilter: ['class']
                    });
                    queueUrgentCleanup();
                }
            };
            const startPlayerBootstrapObservation = () => {
                if (!isEnabled() || observedPlayer || playerBootstrapObserver
                    || !document.documentElement || !isYouTubeWatch()) return;
                playerBootstrapObserver = new MutationObserver(() => {
                    observePlayer();
                    if (observedPlayer) {
                        cleanPlayerAds();
                        queueCleanup();
                    }
                });
                playerBootstrapObserver.observe(document.documentElement, {
                    childList: true,
                    subtree: true
                });
                playerBootstrapTimer = window.setTimeout(
                    stopPlayerBootstrapObservation,
                    10_000);
            };
            const onUserInteraction = event => {
                if (!event.isTrusted || !isYouTubeWatch()) return;
                const video = document.querySelector('video.html5-main-video, video');
                if (!video) return;

                if (event.type === 'keydown') {
                    if (event.key === ' ' || event.key === 'k' || event.key === 'K') {
                        const tag = event.target?.tagName?.toLowerCase();
                        if (tag === 'input' || tag === 'textarea' || event.target?.isContentEditable) return;
                        userManuallyPaused = !video.paused;
                        if (!userManuallyPaused) {
                            lastPlayAttempt = 0;
                            triggerInstantPlay();
                        }
                    }
                    return;
                }

                if (event.type === 'click') {
                    const largePlay = event.target?.closest?.('.ytp-large-play-button');
                    if (largePlay) {
                        userManuallyPaused = false;
                        lastPlayAttempt = 0;
                        autoplayAttempts = 0;
                        try {
                            const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
                            if (typeof player?.playVideo === 'function') player.playVideo();
                        } catch (_) { }
                        return;
                    }
                    const playBtn = event.target?.closest?.('.ytp-play-button');
                    const nonPlayControl = event.target?.closest?.(
                        'button, a, input, [role="button"], .ytp-chrome-bottom, .ytp-chrome-top, '
                        + '.ytp-popup, .ytp-ad-module, .ytp-cards-teaser, .ytp-ce-element, .ytp-contextmenu');
                    if (nonPlayControl && !playBtn) return;
                    const playerEl = event.target?.closest?.('#movie_player, .html5-video-player');
                    if (playBtn || playerEl) {
                        userManuallyPaused = !video.paused;
                        if (!userManuallyPaused) {
                            lastPlayAttempt = 0;
                            triggerInstantPlay();
                        }
                    }
                }
            };
            const onVisibilityChange = () => {
                if (!document.hidden) queueCleanup();
                scheduleFallback(false);
            };
            const onNavigationStart = () => {
                // Revert the ad-time mute and speed-up before dropping the flag,
                // otherwise the next video inherits them.
                restoreAfterAd();
                userManuallyPaused = false;
                activeWatchVideoId = '';
                autoplayAttempts = 0;
                autoplayCompletedForVideoId = '';
                lastPlayAttempt = 0;
                watchPageLoadTime = performance.now();
                clearRecoveryMask();
                if (activeRecoveryMarker) setRecoveryMarker('');
                recoveryVideoId = '';
                recoveryAttempt = 0;
                serverRecoveryExhausted = false;
                seenRecoveryResponses = new WeakSet();
                transportRecoveryVideoId = '';
                transportRecoveryAttempt = 0;
                transportRecoveryResponse = null;
                transportRecoveryAttemptAt = 0;
                transportRecoveryStartSeconds = 0;
                transportRecoveryBaselineTime = Number.NaN;
                transportRecoveryBaselineBufferedEnd = 0;
                transportRecoveryHasPlayed = false;
                transportRecoveryTerminal = false;
                transportRecoveryErrorNode = null;
                transportRecoveryFastPollUntil = 0;
                pendingRecoverySignal = false;
            };
            const onNavigationFinish = () => {
                userManuallyPaused = false;
                activeWatchVideoId = watchQuery().get('v') || '';
                autoplayAttempts = 0;
                autoplayCompletedForVideoId = '';
                lastPlayAttempt = 0;
                watchPageLoadTime = performance.now();
                if (isYouTubeWatch()) {
                    resetTransportRecovery();
                } else {
                    clearRecoveryMask();
                }
                observePlayer();
                startPlayerBootstrapObservation();
                cleanPlayerAds();
                triggerInstantPlay();
                scheduleInstantPlayCheck();
                queueCleanup();
            };
            const startCleanup = () => {
                if (!isEnabled() || cleanupStarted || !document.documentElement) return;
                cleanupStarted = true;
                watchPageLoadTime = performance.now();
                const activity = cleanPlayerAds();
                triggerInstantPlay();
                scheduleInstantPlayCheck();
                playerObserver = new MutationObserver(() => {
                    const player = observedPlayer || document.getElementById('movie_player');
                    if (player?.classList?.contains('ad-showing') || player?.classList?.contains('ad-interrupting')) {
                        queueUrgentCleanup();
                    } else {
                        queueCleanup();
                    }
                });
                observePlayer();
                startPlayerBootstrapObservation();
                for (const eventName of cleanupEvents) {
                    document.addEventListener(eventName, queueCleanup, { passive: true });
                }
                document.addEventListener('yt-navigate-start', onNavigationStart, { passive: true });
                document.addEventListener('yt-navigate-finish', onNavigationFinish, { passive: true });
                try { window.addEventListener?.('popstate', onNavigationStart, { passive: true }); } catch (_) { }
                document.addEventListener('visibilitychange', onVisibilityChange, { passive: true });
                document.addEventListener('keydown', onUserInteraction, true);
                document.addEventListener('click', onUserInteraction, true);
                scheduleFallback(activity);
                stopCleanup = () => {
                    removeVideoHooks();
                    playerObserver?.disconnect();
                    stopPlayerBootstrapObservation();
                    window.clearTimeout(fallbackTimer);
                    window.clearTimeout(cleanupSignalTimer);
                    window.clearTimeout(instantPlayTimer);
                    window.clearTimeout(urgentCleanupTimer);
                    instantPlayTimer = 0;
                    urgentCleanupTimer = 0;
                    cleanupQueued = false;
                    urgentCleanupQueued = false;
                    clickedSkipButtons.clear();
                    for (const eventName of cleanupEvents) {
                        document.removeEventListener(eventName, queueCleanup);
                    }
                    document.removeEventListener('yt-navigate-start', onNavigationStart);
                    document.removeEventListener('yt-navigate-finish', onNavigationFinish);
                    try { window.removeEventListener?.('popstate', onNavigationStart); } catch (_) { }
                    document.removeEventListener('visibilitychange', onVisibilityChange);
                    document.removeEventListener('keydown', onUserInteraction, true);
                    document.removeEventListener('click', onUserInteraction, true);
                };
            };
            startCleanup();
            if (!cleanupStarted) {
                const cleanupBootstrapObserver = new MutationObserver(() => {
                    startCleanup();
                    if (cleanupStarted || !isEnabled()) {
                        cleanupBootstrapObserver.disconnect();
                    }
                });
                bootstrapObservers.push(cleanupBootstrapObserver);
                cleanupBootstrapObserver.observe(document, { childList: true });
            }
            if (!cleanupStarted && document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', startCleanup, { once: true });
            }

            // Keep the remotely compiled cosmetic style independently removable.
            void remoteStyleId;
        })();
        """;
}
