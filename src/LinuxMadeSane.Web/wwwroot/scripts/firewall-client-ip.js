// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

export async function getCloudflareClientIp() {
    try {
        // Cloudflare serves this at the edge, before the tunnel rewrites the source address.
        // Fetch from the browser and the current origin: a server-side lookup would return the server's IP.
        const response = await fetch(new URL("/cdn-cgi/trace", window.location.origin), {
            cache: "no-store",
            credentials: "omit",
            redirect: "error",
            referrerPolicy: "no-referrer",
            signal: AbortSignal.timeout(5000)
        });
        if (!response.ok || !response.headers.get("content-type")?.toLowerCase().startsWith("text/plain")) {
            return null;
        }

        const trace = await response.text();
        if (trace.length > 8192) {
            return null;
        }

        const fields = new Map();
        for (const line of trace.split(/\r?\n/)) {
            const separator = line.indexOf("=");
            if (separator > 0) {
                const key = line.slice(0, separator);
                if (fields.has(key)) {
                    return null;
                }
                fields.set(key, line.slice(separator + 1).trim());
            }
        }

        if (fields.get("h")?.toLowerCase() !== window.location.hostname.toLowerCase() || !fields.get("colo")) {
            return null;
        }

        // The server validates and normalizes IPv4/IPv6 before offering this in the rule editor.
        return fields.get("ip") || null;
    } catch {
        return null;
    }
}
