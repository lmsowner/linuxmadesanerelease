/* Copyright (c) Richard D. Kiernan.
 * Licensed under the Business Source License 1.1. See LICENSE for details. */

window.lmsMediaHlsPreview = (() => {
    const players = new Map();
    const loadingScripts = new Map();

    function loadScriptOnce(src, isReady) {
        if (isReady()) {
            return Promise.resolve(true);
        }

        const existing = loadingScripts.get(src);
        if (existing) {
            return existing;
        }

        const promise = new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = src;
            script.async = true;
            script.onload = () => resolve(true);
            script.onerror = () => reject(new Error(`Unable to load ${src}`));
            document.head.appendChild(script);
        });

        loadingScripts.set(src, promise);
        return promise;
    }

    function getVideo(videoId) {
        const video = document.getElementById(videoId);
        if (!(video instanceof HTMLVideoElement)) {
            throw new Error("Media preview video host is missing.");
        }

        return video;
    }

    function notify(entry, kind, message) {
        if (!entry?.callbacks || !message) {
            return;
        }

        const method = kind === "error"
            ? "OnMediaPreviewPlayerError"
            : "OnMediaPreviewPlayerStatus";
        entry.callbacks.invokeMethodAsync(method, message).catch(() => null);
    }

    function clamp(value, min, max) {
        const number = Number(value);
        if (!Number.isFinite(number)) {
            return min;
        }

        return Math.min(Math.max(number, min), max);
    }

    function formatTime(seconds) {
        const safeSeconds = Math.max(0, Math.floor(Number(seconds) || 0));
        const hours = Math.floor(safeSeconds / 3600);
        const minutes = Math.floor((safeSeconds % 3600) / 60);
        const remainder = safeSeconds % 60;
        return hours > 0
            ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
            : `${minutes}:${String(remainder).padStart(2, "0")}`;
    }

    function withStart(url, startSeconds) {
        const nextUrl = new URL(url, window.location.href);
        nextUrl.searchParams.set("start", Math.max(0, startSeconds).toFixed(3));
        return nextUrl.toString();
    }

    function getScrubber(entry) {
        const scrubber = entry?.options?.scrubberId
            ? document.getElementById(entry.options.scrubberId)
            : null;
        if (!scrubber) {
            return null;
        }

        return {
            scrubber,
            input: scrubber.querySelector("[data-lms-media-seek]"),
            current: scrubber.querySelector("[data-lms-media-current]"),
            duration: scrubber.querySelector("[data-lms-media-duration]")
        };
    }

    function setScrubberVisible(entry, visible) {
        const controls = getScrubber(entry);
        if (!controls) {
            return;
        }

        controls.scrubber.hidden = !visible;
    }

    function updateScrubber(entry, currentSeconds) {
        const duration = Number(entry?.durationSeconds);
        if (!Number.isFinite(duration) || duration <= 0) {
            setScrubberVisible(entry, false);
            return;
        }

        const controls = getScrubber(entry);
        if (!controls) {
            return;
        }

        const current = clamp(currentSeconds, 0, duration);
        controls.scrubber.hidden = false;
        if (controls.input && !entry.scrubbing) {
            controls.input.max = Math.ceil(duration).toString();
            controls.input.value = Math.floor(current).toString();
        }

        if (controls.current) {
            controls.current.textContent = formatTime(current);
        }

        if (controls.duration) {
            controls.duration.textContent = formatTime(duration);
        }
    }

    async function startFallbackStream(videoId, reason, startSeconds, shouldPlay) {
        const entry = players.get(videoId);
        const fallbackUrl = entry?.options?.fallbackUrl;
        if (!entry || entry.fallbackStarted || !fallbackUrl) {
            return false;
        }

        const duration = Number(entry.durationSeconds);
        const start = Number.isFinite(duration) && duration > 0
            ? clamp(startSeconds, 0, Math.max(0, duration - 0.5))
            : Math.max(0, Number(startSeconds) || 0);

        entry.fallbackStarted = true;
        entry.mode = "fallback";
        entry.seekOffset = start;
        entry.lastHlsError = null;
        notify(entry, "status", reason);
        entry.hls?.destroy?.();
        entry.hls = null;

        if (entry.callbacks && !entry.hlsClosed) {
            entry.hlsClosed = true;
            await entry.callbacks.invokeMethodAsync("OnMediaPreviewHlsFallback").catch(() => null);
        }

        if (players.get(videoId) !== entry) {
            return true;
        }

        updateScrubber(entry, start);
        entry.video.src = withStart(fallbackUrl, start);
        entry.video.load();
        if (shouldPlay) {
            await play(videoId, { allowMutedRetry: entry.options?.allowMutedAutoplay !== false });
        }

        return true;
    }

    async function switchToFallback(videoId, reason) {
        const entry = players.get(videoId);
        if (!entry) {
            return false;
        }

        entry.fallbackStarted = false;
        return startFallbackStream(videoId, `${reason}. Switching to compatible preview.`, 0, Boolean(entry.options?.autoPlay));
    }

    async function seekFallback(videoId, startSeconds) {
        const entry = players.get(videoId);
        if (!entry || entry.mode !== "fallback") {
            return;
        }

        const shouldPlay = !entry.video.paused || Boolean(entry.options?.autoPlay);
        entry.fallbackStarted = false;
        await startFallbackStream(videoId, "Seeking preview.", startSeconds, shouldPlay);
    }

    function release(videoId) {
        const entry = players.get(videoId);
        if (entry) {
            entry.controller?.abort?.();
            entry.hls?.destroy?.();
            setScrubberVisible(entry, false);
            players.delete(videoId);
        }

        const video = document.getElementById(videoId);
        if (video instanceof HTMLVideoElement) {
            video.pause();
            video.removeAttribute("src");
            video.load();
        }
    }

    async function load(videoId, url, options, callbacks) {
        release(videoId);

        const video = getVideo(videoId);
        video.muted = options?.allowMutedAutoplay !== false;
        video.autoplay = Boolean(options?.autoPlay);
        video.playsInline = true;

        const controller = new AbortController();
        const durationSeconds = Number(options?.durationSeconds);
        const entry = {
            video,
            callbacks,
            hls: null,
            controller,
            options,
            durationSeconds: Number.isFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0,
            mode: "hls",
            seekOffset: 0
        };
        players.set(videoId, entry);
        setScrubberVisible(entry, false);
        notify(entry, "status", "Loading HLS preview.");

        video.addEventListener("playing", () => {
            notify(players.get(videoId), "status", video.muted ? "Playing muted preview." : "Playing preview.");
        }, { signal: controller.signal });

        video.addEventListener("waiting", () => {
            notify(players.get(videoId), "status", "Buffering preview.");
        }, { signal: controller.signal });

        video.addEventListener("loadedmetadata", () => {
            const activeEntry = players.get(videoId);
            if (!activeEntry) {
                return;
            }

            if ((!activeEntry.durationSeconds || activeEntry.durationSeconds <= 0) &&
                Number.isFinite(video.duration) &&
                video.duration > 0) {
                activeEntry.durationSeconds = video.duration;
            }

            if (activeEntry.mode === "fallback") {
                updateScrubber(activeEntry, activeEntry.seekOffset + video.currentTime);
            }
        }, { signal: controller.signal });

        video.addEventListener("timeupdate", () => {
            const activeEntry = players.get(videoId);
            if (activeEntry?.mode === "fallback" && !activeEntry.scrubbing) {
                updateScrubber(activeEntry, activeEntry.seekOffset + video.currentTime);
            }
        }, { signal: controller.signal });

        video.addEventListener("error", () => {
            const activeEntry = players.get(videoId);
            const code = video.error?.code ? ` (${video.error.code})` : "";
            const prefix = activeEntry?.lastHlsError ? `${activeEntry.lastHlsError}. ` : "";
            notify(activeEntry, "error", `${prefix}Browser media error${code}.`);
        }, { signal: controller.signal });

        const scrubber = getScrubber(entry);
        if (scrubber?.input) {
            scrubber.input.addEventListener("input", () => {
                entry.scrubbing = true;
                updateScrubber(entry, Number(scrubber.input.value));
            }, { signal: controller.signal });

            scrubber.input.addEventListener("change", () => {
                entry.scrubbing = false;
                seekFallback(videoId, Number(scrubber.input.value)).catch(error => {
                    notify(players.get(videoId), "error", error?.message || "Preview seek failed.");
                });
            }, { signal: controller.signal });
        }

        await loadScriptOnce(
            "/lib/media-player/vendor/hls.min.js",
            () => Boolean(window.Hls));

        if (!window.Hls?.isSupported?.()) {
            if (await switchToFallback(videoId, "HLS is not available in this browser")) {
                return;
            }

            if (video.canPlayType("application/vnd.apple.mpegurl")) {
                video.src = url;
                video.load();
                notify(entry, "status", "Starting native HLS preview.");
                if (options?.autoPlay) {
                    await play(videoId, { allowMutedRetry: options?.allowMutedAutoplay !== false });
                }

                return;
            }

            throw new Error("This browser does not support HLS preview playback.");
        }

        const hls = new window.Hls({
            lowLatencyMode: false,
            maxBufferLength: 30,
            maxMaxBufferLength: 60,
            manifestLoadingMaxRetry: 4,
            levelLoadingMaxRetry: 4,
            fragLoadingMaxRetry: 4,
            xhrSetup: xhr => {
                xhr.withCredentials = true;
            }
        });

        entry.hls = hls;
        hls.on(window.Hls.Events.ERROR, (_, data) => {
            const detailParts = [
                data?.details || data?.type || "HLS playback error",
                data?.response?.code ? `HTTP ${data.response.code}` : "",
                data?.reason || ""
            ].filter(Boolean);
            const detail = detailParts.join(" ");
            const activeEntry = players.get(videoId);
            if (activeEntry) {
                activeEntry.lastHlsError = detail;
            }

            if (data?.details === "bufferAddCodecError") {
                switchToFallback(videoId, "This browser rejected the HLS preview").catch(error => {
                    notify(players.get(videoId), "error", error?.message || "WebM preview fallback failed.");
                });
                return;
            }

            notify(activeEntry, data?.fatal ? "error" : "status", detail);
            if (data?.fatal) {
                hls.destroy();
            }
        });

        hls.on(window.Hls.Events.MEDIA_ATTACHED, () => {
            hls.loadSource(url);
        });

        hls.on(window.Hls.Events.MANIFEST_PARSED, async () => {
            notify(players.get(videoId), "status", "Preview stream ready.");
            if (options?.autoPlay) {
                await play(videoId, { allowMutedRetry: options?.allowMutedAutoplay !== false });
            }
        });

        hls.on(window.Hls.Events.FRAG_BUFFERED, () => {
            notify(players.get(videoId), "status", "Preview buffered.");
        });

        hls.attachMedia(video);
    }

    async function play(videoId, options) {
        const entry = players.get(videoId);
        if (!entry?.video) {
            throw new Error("Media preview player is not loaded yet.");
        }

        notify(entry, "status", "Starting preview.");
        try {
            const result = entry.video.play();
            if (result?.then) {
                await result;
            }
        } catch (error) {
            if (options?.allowMutedRetry && !entry.video.muted) {
                entry.video.muted = true;
                notify(entry, "status", "Autoplay was blocked. Retrying muted preview.");
                const result = entry.video.play();
                if (result?.then) {
                    await result;
                }

                return;
            }

            throw error;
        }
    }

    return {
        load,
        play,
        release
    };
})();
