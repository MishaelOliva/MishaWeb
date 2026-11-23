namespace MishaWeb;

internal static class BrowserPerformance
{
    public static readonly string NoMotionDocumentScript =
        """
        (() => {
            const styleId = '__misha_no_motion_style';
            const installStyle = () => {
                const root = document.documentElement;
                if (!root || document.getElementById(styleId)) return;

                const style = document.createElement('style');
                style.id = styleId;
                style.textContent = `
                    *, *::before, *::after {
                        animation-delay: 0s !important;
                        animation-duration: 0.001ms !important;
                        animation-iteration-count: 1 !important;
                        transition-delay: 0s !important;
                        transition-duration: 0.001ms !important;
                        scroll-behavior: auto !important;
                    }
                `;
                (document.head || root).appendChild(style);
            };

            installStyle();
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', installStyle, { once: true });
            }
        })();
        """;

    public const string RestoreMotionDocumentScript =
        "document.getElementById('__misha_no_motion_style')?.remove();";

    public static readonly string SiteResponsivenessDocumentScript =
        """
        (() => {
            if (window.__mishaSiteResponsivenessInstalled) return;
            try {
                Object.defineProperty(window, '__mishaSiteResponsivenessInstalled', {
                    value: true,
                    writable: false,
                    configurable: false
                });
            } catch (_) {
                window.__mishaSiteResponsivenessInstalled = true;
            }

            // 1. Safe, Selective Passive Event Listeners for Scrolling
            // We ONLY mark default scroll events on Window/Document/Body/DocumentElement as passive when
            // the caller did NOT explicitly pass { passive: false }.
            // We NEVER override explicit { passive: false }, and we NEVER touch interactive
            // surfaces (canvas, maps, sliders, games, contenteditable) that require preventDefault.
            try {
                const scrollTypes = new Set(['wheel', 'mousewheel', 'touchstart', 'touchmove']);
                const originalAddEventListener = EventTarget.prototype.addEventListener;
                EventTarget.prototype.addEventListener = function (type, listener, options) {
                    if (scrollTypes.has(type)) {
                        const isExplicitActive = typeof options === 'object' && options !== null && options.passive === false;
                        if (!isExplicitActive) {
                            const isGlobalTarget = this === window || this === document || this === document.body || this === document.documentElement;
                            if (isGlobalTarget) {
                                if (typeof options === 'boolean') {
                                    options = { capture: options, passive: true };
                                } else if (typeof options === 'object' && options !== null) {
                                    if (options.passive === undefined) {
                                        options = Object.assign({}, options, { passive: true });
                                    }
                                } else {
                                    options = { passive: true };
                                }
                            }
                        }
                    }
                    return originalAddEventListener.call(this, type, listener, options);
                };
            } catch (_) {}

            // 2. Off-Thread Asynchronous Image Decoding
            // Applied non-destructively without mutating DOM attributes to avoid hydration issues
            try {
                if ('HTMLImageElement' in window) {
                    const originalSrcDescriptor = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'src');
                    if (originalSrcDescriptor && originalSrcDescriptor.set) {
                        const originalSet = originalSrcDescriptor.set;
                        Object.defineProperty(HTMLImageElement.prototype, 'src', {
                            configurable: true,
                            enumerable: true,
                            get: originalSrcDescriptor.get,
                            set: function (val) {
                                if (this.decoding === 'auto' || !this.decoding) {
                                    try { this.decoding = 'async'; } catch (_) {}
                                }
                                return originalSet.call(this, val);
                            }
                        });
                    }
                }
            } catch (_) {}

            // 3. Bounded, Leak-Free Hover/Press/Focus Intent Link Preconnect / DNS-Prefetch
            try {
                const preconnectedOrigins = new Set();
                const activePreconnectLinks = [];
                const maxPreconnectCount = 10;
                let hoverTimer = null;
                let currentAnchor = null;

                const clearHoverTimer = () => {
                    if (hoverTimer !== null) {
                        clearTimeout(hoverTimer);
                        hoverTimer = null;
                    }
                };

                const getEligiblePreconnectOrigin = (anchor) => {
                    if (!anchor) return null;
                    try {
                        const protocol = anchor.protocol;
                        if (protocol !== 'http:' && protocol !== 'https:') return null;
                        const origin = anchor.origin;
                        if (!origin || origin === 'null' || origin === location.origin || preconnectedOrigins.has(origin)) {
                            return null;
                        }
                        return origin;
                    } catch (_) {
                        return null;
                    }
                };

                const preconnectAnchor = (anchor, resolvedOrigin) => {
                    if (!anchor || document.hidden) return;
                    try {
                        const targetOrigin = resolvedOrigin || getEligiblePreconnectOrigin(anchor);
                        if (!targetOrigin || targetOrigin === location.origin || preconnectedOrigins.has(targetOrigin)) return;

                        preconnectedOrigins.add(targetOrigin);
                        const head = document.head || document.documentElement;
                        if (!head) return;

                        // Prune oldest if at capacity to prevent memory leaks in SPAs
                        if (activePreconnectLinks.length >= maxPreconnectCount) {
                            const oldest = activePreconnectLinks.shift();
                            if (oldest) {
                                preconnectedOrigins.delete(oldest.origin);
                                oldest.links.forEach(el => el.remove());
                            }
                        }

                        const dnsEl = document.createElement('link');
                        dnsEl.rel = 'dns-prefetch';
                        dnsEl.href = targetOrigin;
                        head.appendChild(dnsEl);

                        const preconnectEl = document.createElement('link');
                        preconnectEl.rel = 'preconnect';
                        preconnectEl.href = targetOrigin;
                        head.appendChild(preconnectEl);

                        activePreconnectLinks.push({ origin: targetOrigin, links: [dnsEl, preconnectEl] });
                    } catch (_) {}
                };

                document.addEventListener('mouseover', (e) => {
                    const target = e.target;
                    if (currentAnchor && target && typeof currentAnchor.contains === 'function' && currentAnchor.contains(target)) {
                        return;
                    }
                    const anchor = target && typeof target.closest === 'function' ? target.closest('a[href]') : null;
                    if (anchor === currentAnchor) return;
                    currentAnchor = anchor;
                    clearHoverTimer();
                    if (!anchor || document.hidden) return;
                    const targetOrigin = getEligiblePreconnectOrigin(anchor);
                    if (!targetOrigin) return;

                    hoverTimer = setTimeout(() => {
                        hoverTimer = null;
                        if (currentAnchor !== anchor) return;
                        preconnectAnchor(anchor, targetOrigin);
                    }, 65);
                }, { passive: true });

                const preconnectEventTarget = (e) => {
                    const target = e.target;
                    const anchor = target && typeof target.closest === 'function' ? target.closest('a[href]') : null;
                    if (anchor) {
                        clearHoverTimer();
                        preconnectAnchor(anchor);
                    }
                };
                document.addEventListener('mousedown', preconnectEventTarget, { passive: true });
                document.addEventListener('focusin', preconnectEventTarget, { passive: true });

                document.addEventListener('mouseout', (e) => {
                    if (!currentAnchor) return;
                    const related = e.relatedTarget;
                    if (related && currentAnchor.contains(related)) return;
                    currentAnchor = null;
                    clearHoverTimer();
                }, { passive: true });
            } catch (_) {}
        })();
        """;
}
