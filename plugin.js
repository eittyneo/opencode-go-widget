/**
 * OpenCode Go desktop widget — OpenCode plugin (server side)
 *
 * Runs inside the OpenCode server. It:
 *  1. Reads your OpenCode Console sign-in through OpenCode's own integration API
 *     (the token is refreshed by OpenCode when needed).
 *  2. Calls the OpenCode Go usage API: {console}/api/go/status
 *     and normalizes the 5-hour / weekly / monthly meters.
 *  3. Serves the result on http://127.0.0.1:19876/quota for the desktop widget.
 *  4. Writes quota-live.json (cache) and keeps the widget process in sync with
 *     the OpenCode desktop app (auto launch / exit).
 *  5. Registers the /widget command to reopen the floating widget.
 *
 * No third-party quota plugin is required; this talks to the OpenCode Console
 * API directly. The access token never leaves this machine except when calling
 * opencode.ai endpoints.
 */
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { spawn, execFile } from "node:child_process";
import { fileURLToPath } from "node:url";
import { Plugin } from "@opencode/plugin";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const LIVE_CACHE_PATH = path.join(__dirname, "quota-live.json");
const WIDGET_EXE_PATH = path.join(__dirname, "OpenCodeQuotaWidget.exe");
const PORT = 19876;

function formatTime(date = new Date()) {
  const hh = String(date.getHours()).padStart(2, "0");
  const mm = String(date.getMinutes()).padStart(2, "0");
  const ss = String(date.getSeconds()).padStart(2, "0");
  return `${hh}:${mm}:${ss}`;
}

function parseMeter(key, label, meterObj, fallbackResetsAt) {
  const limitMicro = Number(meterObj?.limitMicroCents ?? 0);
  const usedMicro = Number(meterObj?.usedMicroCents ?? 0);
  const limitUsd = limitMicro / 1e8;
  const usedUsd = usedMicro / 1e8;
  const remainUsd = Math.max(0, limitUsd - usedUsd);
  const usedPercent = limitMicro > 0 ? Math.min(100, Math.max(0, (usedMicro / limitMicro) * 100)) : 0;
  const remainPercent = Math.max(0, 100 - usedPercent);
  const resetsAt = meterObj?.resetsAt || fallbackResetsAt || "";
  return {
    key,
    label,
    usedUsd: Number(usedUsd.toFixed(2)),
    limitUsd: Number(limitUsd.toFixed(2)),
    remainUsd: Number(remainUsd.toFixed(2)),
    usedPercent: Number(usedPercent.toFixed(1)),
    remainPercent: Number(remainPercent.toFixed(1)),
    resetsAt,
  };
}

async function fetchQuotaFromContext(ctx) {
  try {
    const connection = await ctx.integration.connection.active("opencode");
    if (!connection) {
      return {
        ok: false,
        plan: "OpenCode Go",
        updatedAt: formatTime(),
        error: "No OpenCode sign-in found. Open OpenCode and sign in first.",
        meters: [],
      };
    }

    const cred = await ctx.integration.connection.resolve(connection);
    const accessToken = cred?.access || cred?.key;
    if (!accessToken) {
      return {
        ok: false,
        plan: "OpenCode Go",
        updatedAt: formatTime(),
        error: "Could not resolve the OpenCode access token.",
        meters: [],
      };
    }

    const metadata = cred?.metadata || {};
    const baseUrl = metadata.server || "https://opencode.ai/console";
    const headers = {
      Authorization: `Bearer ${accessToken}`,
      Accept: "application/json",
      ...(metadata.orgID ? { "x-org-id": metadata.orgID } : {}),
    };

    const res = await fetch(`${baseUrl}/api/go/status`, {
      method: "GET",
      headers,
      signal: AbortSignal.timeout(8000),
    });

    if (!res.ok) {
      return {
        ok: false,
        plan: "OpenCode Go",
        updatedAt: formatTime(),
        error: `Console API error (${res.status}).`,
        meters: [],
      };
    }

    const data = await res.json();
    const access = data?.access;
    const meters = access?.meters;
    if (!meters) {
      return {
        ok: false,
        plan: "OpenCode Go",
        updatedAt: formatTime(),
        error: "This account has no active OpenCode Go subscription.",
        meters: [],
      };
    }

    const planName = data?.product === "go-plus" ? "OpenCode Go Plus" : "OpenCode Go";
    const endsAt = access?.endsAt || "";

    const result = {
      ok: true,
      plan: planName,
      updatedAt: formatTime(),
      meters: [
        parseMeter("fiveHour", "5h", meters.fiveHour, endsAt),
        parseMeter("week", "Weekly", meters.week, endsAt),
        parseMeter("month", "Monthly", meters.month, endsAt),
      ],
    };

    try {
      fs.writeFileSync(LIVE_CACHE_PATH, JSON.stringify(result, null, 2), "utf8");
    } catch {}

    return result;
  } catch (err) {
    return {
      ok: false,
      plan: "OpenCode Go",
      updatedAt: formatTime(),
      error: err instanceof Error ? err.message : String(err),
      meters: [],
    };
  }
}

function checkProcessRunning(imageName) {
  return new Promise((resolve) => {
    execFile(
      "tasklist.exe",
      ["/FI", `IMAGENAME eq ${imageName}`, "/NH"],
      { windowsHide: true, timeout: 3000 },
      (err, stdout) => {
        if (err || !stdout) return resolve(false);
        resolve(stdout.toLowerCase().includes(imageName.toLowerCase()));
      }
    );
  });
}

function launchWidget() {
  if (!fs.existsSync(WIDGET_EXE_PATH)) return;
  try {
    const child = spawn(WIDGET_EXE_PATH, [], {
      cwd: __dirname,
      detached: true,
      stdio: "ignore",
      windowsHide: false,
    });
    child.unref();
  } catch {}
}

export default Plugin.define({
  id: "opencode.go.floating-widget",
  async setup(ctx) {
    let lastSnapshot = null;
    let inFlight = null;

    const getQuota = async (forceRefresh = false) => {
      if (!forceRefresh && lastSnapshot && lastSnapshot.ok) {
        return lastSnapshot;
      }
      if (inFlight) return inFlight;
      inFlight = fetchQuotaFromContext(ctx)
        .then((res) => {
          if (res.ok || !lastSnapshot) {
            lastSnapshot = res;
          }
          return lastSnapshot;
        })
        .finally(() => {
          inFlight = null;
        });
      return inFlight;
    };

    // Initial quota fetch
    void getQuota(true);

    // Local HTTP server for instant widget refresh
    let server = null;
    try {
      server = http.createServer(async (req, res) => {
        res.setHeader("Content-Type", "application/json; charset=utf-8");
        res.setHeader("Access-Control-Allow-Origin", "*");
        const url = new URL(req.url || "/", `http://127.0.0.1:${PORT}`);
        if (url.pathname === "/quota") {
          const force = url.searchParams.get("refresh") === "1";
          const data = await getQuota(force);
          res.writeHead(200);
          res.end(JSON.stringify(data));
          return;
        }
        if (url.pathname === "/launch") {
          launchWidget();
          res.writeHead(200);
          res.end(JSON.stringify({ launched: true }));
          return;
        }
        res.writeHead(404);
        res.end(JSON.stringify({ error: "Not found" }));
      });
      server.on("error", () => {});
      server.listen(PORT, "127.0.0.1");
    } catch {}

    // Register /widget slash command to reopen the floating card anytime
    let cmdReg = null;
    try {
      cmdReg = await ctx.command.transform((editor) => {
        editor.add({
          name: "widget",
          description: "Open the OpenCode Go desktop quota widget",
          execute: async () => {
            await getQuota(true);
            launchWidget();
          },
        });
      });
    } catch {}

    // Auto-launch widget when OpenCode Desktop GUI starts
    let wasOpenCodeRunning = false;
    const syncWidgetWithDesktop = async () => {
      const isOpenCodeRunning = await checkProcessRunning("OpenCode.exe");
      if (isOpenCodeRunning && !wasOpenCodeRunning) {
        const isWidgetRunning = await checkProcessRunning("OpenCodeQuotaWidget.exe");
        if (!isWidgetRunning) {
          launchWidget();
        }
      }
      wasOpenCodeRunning = isOpenCodeRunning;
    };

    void syncWidgetWithDesktop();
    const processTimer = setInterval(() => {
      void syncWidgetWithDesktop();
    }, 3000);

    // Subscribe to OpenCode events to refresh quota after model turns
    const abortController = new AbortController();
    let refreshDebounce = null;
    void (async () => {
      try {
        for await (const event of ctx.event.subscribe({ signal: abortController.signal })) {
          const type = String(event?.type || "");
          if (type.includes("idle") || type.includes("completed") || type.includes("finish")) {
            if (refreshDebounce) clearTimeout(refreshDebounce);
            refreshDebounce = setTimeout(() => {
              void getQuota(true);
            }, 2500);
          }
        }
      } catch {}
    })();

    return async () => {
      clearInterval(processTimer);
      if (refreshDebounce) clearTimeout(refreshDebounce);
      abortController.abort();
      if (cmdReg) {
        try {
          await cmdReg.dispose();
        } catch {}
      }
      if (server) {
        try {
          server.close();
        } catch {}
      }
    };
  },
});
