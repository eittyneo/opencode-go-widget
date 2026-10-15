/**
 * OpenCode Go desktop widget — standalone quota fetcher (fallback path)
 *
 * Used by the widget when the OpenCode plugin's local HTTP endpoint
 * (127.0.0.1:19876) is not available (for example when the widget is started
 * without OpenCode). It reads the OpenCode Console sign-in directly from
 * OpenCode's credential database (~/.local/share/opencode/opencode.db) and
 * calls the Go usage API. It never refreshes tokens, so an expired sign-in
 * stays expired here until OpenCode refreshes it.
 *
 * Requires Node.js >= 22.5 (uses the built-in node:sqlite module).
 */
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const LIVE_CACHE_PATH = path.join(__dirname, "quota-live.json");

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

async function main() {
  try {
    const dbPath = path.join(os.homedir(), ".local", "share", "opencode", "opencode.db");
    if (!fs.existsSync(dbPath)) {
      throw new Error("opencode.db not found. Is OpenCode installed and signed in?");
    }

    const db = new DatabaseSync(dbPath, { open: true, readOnly: true });
    const rows = db
      .prepare(
        "SELECT value FROM credential WHERE integration_id = 'opencode' ORDER BY active DESC, time_updated DESC LIMIT 1"
      )
      .all();
    db.close();

    if (!rows || rows.length === 0) {
      throw new Error("No OpenCode Console sign-in found in the credential database.");
    }

    const parsed = JSON.parse(rows[0].value);
    const accessToken = parsed?.access || parsed?.key;
    const metadata = parsed?.metadata || {};
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
      throw new Error(`Console API error (${res.status}).`);
    }

    const data = await res.json();
    const access = data?.access;
    const meters = access?.meters;
    if (!meters) {
      throw new Error("This account has no active OpenCode Go subscription.");
    }

    const planName = data?.product === "go-plus" ? "OpenCode Go Plus" : "OpenCode Go";
    const endsAt = access?.endsAt || "";

    const output = {
      ok: true,
      plan: planName,
      updatedAt: formatTime(),
      meters: [
        parseMeter("fiveHour", "5h", meters.fiveHour, endsAt),
        parseMeter("week", "Weekly", meters.week, endsAt),
        parseMeter("month", "Monthly", meters.month, endsAt),
      ],
    };

    fs.writeFileSync(LIVE_CACHE_PATH, JSON.stringify(output, null, 2), "utf8");
    process.stdout.write(JSON.stringify(output));
  } catch (err) {
    const fallback = {
      ok: false,
      plan: "OpenCode Go",
      updatedAt: formatTime(),
      error: err instanceof Error ? err.message : String(err),
      meters: [],
    };
    process.stdout.write(JSON.stringify(fallback));
  }
}

main();
