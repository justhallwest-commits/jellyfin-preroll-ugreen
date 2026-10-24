/*
 * Pre-Roll Videos: web player integration (plugin %PLUGIN_VERSION%)
 *
 * Why this exists:
 *   Jellyfin's web player only asks the server for pre-rolls ("intros") when playback
 *   starts at the very first item of its queue. Pressing Play on an episode queues the
 *   WHOLE series and starts partway in, so only S01E01 ever got a pre-roll, and episodes
 *   that autoplay afterwards never ask at all.
 *
 * What it does:
 *   When you press Play, it asks the plugin which pre-roll(s) belong in front of the item
 *   and the next episodes, and builds the play queue as
 *       pre-roll → episode → pre-roll → next episode → …
 *   so every movie / episode gets its pre-roll, including autoplay, with no flash.
 *   Resumed items keep resuming (no pre-roll in front of a resume), but the episodes that
 *   follow still get theirs.
 *
 *   If anything goes wrong it simply plays normally, and the server-side fallback in the
 *   plugin can still add the pre-roll.
 */
(function () {
    'use strict';

    if (window.__prerollVideos) {
        return;
    }

    var VERSION = '%PLUGIN_VERSION%';
    var LOOKAHEAD = 40;          // how many upcoming items get a pre-roll planned
    var PLAN_TIMEOUT_MS = 5000;  // never hold up playback longer than this
    var TAG = '[Pre-Roll Videos]';

    var state = window.__prerollVideos = {
        version: VERSION,
        hooked: false,
        playbackManager: null,
        lastQueue: null
    };

    function log() {
        try {
            var a = Array.prototype.slice.call(arguments);
            a.unshift(TAG);
            console.log.apply(console, a);
        } catch (e) { /* ignore */ }
    }

    function norm(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function shallowCopy(o) {
        var r = {};
        for (var k in o) {
            if (Object.prototype.hasOwnProperty.call(o, k)) {
                r[k] = o[k];
            }
        }
        return r;
    }

    function cloneItem(item) {
        return JSON.parse(JSON.stringify(item));
    }

    function withTimeout(promise, ms) {
        return new Promise(function (resolve, reject) {
            var done = false;
            var t = setTimeout(function () {
                if (!done) {
                    done = true;
                    reject(new Error('timed out after ' + ms + ' ms'));
                }
            }, ms);
            Promise.resolve(promise).then(function (v) {
                if (!done) {
                    done = true;
                    clearTimeout(t);
                    resolve(v);
                }
            }, function (e) {
                if (!done) {
                    done = true;
                    clearTimeout(t);
                    reject(e);
                }
            });
        });
    }

    function apiServerId(api) {
        try {
            if (typeof api.serverId === 'function') {
                return api.serverId();
            }
            return api.serverInfo().Id;
        } catch (e) {
            return null;
        }
    }

    // ── Status reports (shown in the plugin's "Recent activity") ─────────
    var reported = {};

    function report(status, detail) {
        if (reported[status]) {
            return;
        }
        reported[status] = true;
        var attempts = 0;
        (function send() {
            attempts++;
            var api = window.ApiClient;
            if (!api || typeof api.accessToken !== 'function' || !api.accessToken()) {
                if (attempts < 100) {
                    setTimeout(send, 3000);
                }
                return;
            }
            try {
                Promise.resolve(api.ajax({
                    type: 'POST',
                    url: api.getUrl('PrerollVideos/ClientStatus'),
                    data: JSON.stringify({ Status: status, Detail: detail || '', Version: VERSION }),
                    contentType: 'application/json'
                })).catch(function () { /* ignore */ });
            } catch (e) { /* ignore */ }
        })();
    }

    // ── Find the web app's playback manager ─────────────────────────────
    // jellyfin-web publishes its event helper as window.Events, and the playback
    // manager passes itself to it (Events.on/trigger(playbackManager, ...)).
    function looksLikePlaybackManager(o) {
        try {
            return !!o && typeof o === 'object'
                && typeof o.play === 'function'
                && typeof o.nextTrack === 'function'
                && typeof o.getCurrentPlayer === 'function'
                && typeof o.translateItemsForPlayback === 'function'
                && typeof o.getItemsForPlayback === 'function';
        } catch (e) {
            return false;
        }
    }

    function consider(o) {
        if (!state.hooked && looksLikePlaybackManager(o)) {
            hook(o);
        }
    }

    function patchEvents(ev) {
        if (!ev || ev.__prerollPatched) {
            return;
        }
        try {
            Object.defineProperty(ev, '__prerollPatched', { value: true });
        } catch (e) {
            ev.__prerollPatched = true;
        }
        ['on', 'off', 'trigger'].forEach(function (name) {
            var original = ev[name];
            if (typeof original !== 'function') {
                return;
            }
            ev[name] = function (obj) {
                if (!state.hooked) {
                    consider(obj);
                }
                return original.apply(this, arguments);
            };
        });
    }

    function watchForEvents() {
        if (window.Events) {
            patchEvents(window.Events);
            return;
        }
        var current;
        try {
            Object.defineProperty(window, 'Events', {
                configurable: true,
                enumerable: true,
                get: function () { return current; },
                set: function (v) {
                    current = v;
                    try { patchEvents(v); } catch (e) { /* ignore */ }
                }
            });
        } catch (e) { /* the poll below covers it */ }
    }

    // ── Queue building ───────────────────────────────────────────────────
    function isTarget(item) {
        return !!item && item.MediaType === 'Video' && (item.Type === 'Episode' || item.Type === 'Movie');
    }

    function getItems(pm, serverId, options) {
        if (options.items && options.items.length) {
            return Promise.resolve(options.items.slice());
        }
        if (options.ids && options.ids.length) {
            return Promise.resolve(pm.getItemsForPlayback(serverId, { Ids: options.ids.join(',') }))
                .then(function (r) { return (r && r.Items) || []; });
        }
        return Promise.resolve([]);
    }

    /** Returns new play options with pre-rolls interleaved, or null to play normally. */
    function buildPlayOptions(pm, api, options) {
        var serverId = options.serverId
            || (options.items && options.items[0] && options.items[0].ServerId)
            || apiServerId(api);
        var opts = shallowCopy(options);
        var list, start, first, resuming;

        return getItems(pm, serverId, options).then(function (items) {
            if (!items.length) {
                return null;
            }
            // Same expansion the player does itself (episode → rest of series, season, playlist…).
            return Promise.resolve(pm.translateItemsForPlayback(items, opts));
        }).then(function (translated) {
            if (!translated || !translated.length) {
                return null;
            }
            list = translated.slice();
            start = opts.startIndex || 0;
            if (start < 0 || start >= list.length) {
                start = 0;
            }
            resuming = !!options.startPositionTicks;
            first = resuming ? start + 1 : start;

            var targets = [];
            for (var i = first; i < list.length && targets.length < LOOKAHEAD; i++) {
                if (isTarget(list[i])) {
                    targets.push(list[i].Id);
                }
            }
            if (!targets.length) {
                return null;
            }
            return api.getJSON(api.getUrl('PrerollVideos/Plan', { itemIds: targets.join(',') }));
        }).then(function (plan) {
            if (!plan || !list) {
                return null;
            }
            var enabled = plan.Enabled !== undefined ? plan.Enabled : plan.enabled;
            var planData = plan.Plan || plan.plan || {};
            var prerollIds = plan.PrerollIds || plan.prerollIds || [];
            if (!enabled) {
                return null;
            }

            var planMap = {};
            Object.keys(planData).forEach(function (k) {
                planMap[norm(k)] = planData[k] || [];
            });
            var isClip = {};
            prerollIds.forEach(function (id) {
                isClip[norm(id)] = true;
            });

            var clipIds = [];
            var seen = {};
            Object.keys(planMap).forEach(function (k) {
                planMap[k].forEach(function (c) {
                    if (!seen[norm(c)]) {
                        seen[norm(c)] = true;
                        clipIds.push(c);
                    }
                });
            });
            if (!clipIds.length) {
                return null;
            }

            return Promise.resolve(pm.getItemsForPlayback(serverId, { Ids: clipIds.join(',') })).then(function (r) {
                var clips = {};
                ((r && r.Items) || []).forEach(function (c) {
                    clips[norm(c.Id)] = c;
                });
                if (!Object.keys(clips).length) {
                    report('noclips', 'the pre-roll clips could not be loaded for this user. Check the user has access to the pre-roll library.');
                    return null;
                }

                var queue = [];
                var newStart = 0;
                var added = 0;
                for (var i = 0; i < list.length; i++) {
                    var it = list[i];
                    if (i === start) {
                        newStart = queue.length;
                    }
                    var wanted = i >= first ? planMap[norm(it.Id)] : null;
                    var prev = queue.length ? queue[queue.length - 1] : null;
                    var alreadyPreceded = prev && isClip[norm(prev.Id)];
                    if (wanted && wanted.length && !alreadyPreceded && !isClip[norm(it.Id)]) {
                        for (var j = 0; j < wanted.length; j++) {
                            var c = clips[norm(wanted[j])];
                            if (c) {
                                queue.push(cloneItem(c));
                                added++;
                            }
                        }
                    }
                    queue.push(it);
                }
                if (!added) {
                    return null;
                }

                var out = {
                    items: queue,
                    startIndex: newStart,
                    serverId: serverId,
                    fullscreen: options.fullscreen,
                    __prerollVideos: true
                };

                var picked = list[start];
                if (resuming) {
                    // The picked item plays first and resumes; pre-rolls only before what follows.
                    out.startPositionTicks = options.startPositionTicks;
                    out.mediaSourceId = options.mediaSourceId;
                    out.audioStreamIndex = options.audioStreamIndex;
                    out.subtitleStreamIndex = options.subtitleStreamIndex;
                } else if (picked) {
                    // Audio / subtitle / version choices belong to the episode or movie, not the pre-roll.
                    picked.playOptions = {
                        fullscreen: options.fullscreen !== false,
                        mediaSourceId: options.mediaSourceId,
                        audioStreamIndex: options.audioStreamIndex,
                        subtitleStreamIndex: options.subtitleStreamIndex,
                        startPositionTicks: 0
                    };
                }

                state.lastQueue = queue.map(function (q) {
                    return (isClip[norm(q.Id)] ? '[pre-roll] ' : '') + q.Name;
                });
                log('added ' + added + ' pre-roll(s) to the queue', state.lastQueue.slice(newStart, newStart + 8));
                return out;
            });
        });
    }

    // ── Hook ─────────────────────────────────────────────────────────────
    function hook(pm) {
        state.hooked = true;
        state.playbackManager = pm;
        var originalPlay = pm.play;

        pm.play = function (options) {
            var self = this;
            var args = arguments;
            var api = window.ApiClient;

            function playNormally() {
                return originalPlay.apply(self, args);
            }

            try {
                if (!options || options.__prerollVideos || options.fullscreen === false || !api) {
                    return playNormally();
                }
                var current = typeof pm.getCurrentPlayer === 'function' ? pm.getCurrentPlayer() : null;
                if (current && current.isLocalPlayer === false) {
                    // Casting to another device: that device adds its own pre-rolls.
                    return playNormally();
                }
                var sid = options.serverId || (options.items && options.items[0] && options.items[0].ServerId);
                var mine = apiServerId(api);
                if (sid && mine && sid !== mine) {
                    return playNormally();
                }
            } catch (e) {
                return playNormally();
            }

            return withTimeout(buildPlayOptions(pm, api, options), PLAN_TIMEOUT_MS).then(function (newOptions) {
                return newOptions ? originalPlay.call(self, newOptions) : playNormally();
            }, function (err) {
                log('could not add pre-rolls, playing normally:', (err && err.message) || err);
                return playNormally();
            });
        };

        log('attached to the player (plugin ' + VERSION + ')');
        report('hooked', '');
    }

    // ── Start ────────────────────────────────────────────────────────────
    watchForEvents();

    var polls = 0;
    var timer = setInterval(function () {
        polls++;
        if (window.Events) {
            patchEvents(window.Events);
        }
        if (state.hooked) {
            clearInterval(timer);
            return;
        }
        var api = window.ApiClient;
        var signedIn = api && typeof api.accessToken === 'function' && api.accessToken();
        if (polls >= 180 && signedIn) { // ~90 s after load while signed in
            report('failed', 'could not find the web player. Jellyfin web version may have changed');
            clearInterval(timer);
        }
    }, 500);
})();
