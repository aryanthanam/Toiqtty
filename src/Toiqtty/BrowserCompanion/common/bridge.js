/* No page injection, password access, browser database access, or remote service. */
var api = globalThis.browser ?? globalThis.chrome;
async function connection() { return (await api.storage.local.get("connection")).connection; }
const encoder = new TextEncoder();
const hex = bytes => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("").toUpperCase();
const sha256 = async bytes => hex(await crypto.subtle.digest("SHA-256", bytes));
async function hmac(key, text) {
  const imported = await crypto.subtle.importKey("raw", encoder.encode(key), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", imported, encoder.encode(text)));
}
async function request(path, data, supplied) {
  const saved = supplied ?? await connection();
  if (!saved || !Number.isInteger(saved.port) || saved.port < 1 || saved.port > 65535 || !/^[A-F0-9]{64}$/.test(saved.token)) throw new Error("Connect this browser profile first.");
  // The token never leaves this extension. Requests carry an HMAC over a fresh
  // nonce, and a reply is trusted only when it proves the same key, so another
  // program listening on this port receives neither the token nor any cookies.
  const method = data === undefined ? "GET" : "POST", body = data === undefined ? new Uint8Array() : encoder.encode(JSON.stringify(data));
  const nonce = hex(crypto.getRandomValues(new Uint8Array(32)));
  const mac = await hmac(saved.token, `toiqtty-bridge-v2 request\n${method} ${path}\n${nonce}\n${await sha256(body)}`);
  const response = await fetch(`http://127.0.0.1:${saved.port}${path}`, {
    method, cache: "no-store", credentials: "omit",
    headers: { "Authorization": `Toiqtty-HMAC ${nonce} ${mac}`, ...(data === undefined ? {} : { "Content-Type": "application/json" }) },
    body: data === undefined ? undefined : body, signal: AbortSignal.timeout(8000)
  });
  if (!response.ok) throw new Error(response.status === 401 ? "Connection revoked or pairing code expired. Reconnect in toiqtty." : "toiqtty rejected this request. Check the configured sites.");
  const reply = new Uint8Array(await response.arrayBuffer());
  const expected = await hmac(saved.token, `toiqtty-bridge-v2 response\n${nonce}\n${response.status}\n${await sha256(reply)}`);
  if (response.headers.get("X-Toiqtty-Proof") !== expected) throw new Error("The local endpoint could not prove it is this toiqtty connection. Nothing was shared.");
  return JSON.parse(new TextDecoder().decode(reply));
}
function permissionsFor(origins) {
  // Firefox match patterns reject explicit ports; cookies themselves are host-scoped.
  return [...new Set(origins.map(origin => { const url = new URL(origin); return url.protocol + "//" + url.hostname + "/*"; }))];
}
function regularStore(store) { return store.incognito !== true && store.id !== "1" && store.id !== "firefox-private"; }
function matches(cookie, site) {
  const host = new URL(site).hostname, domain = cookie.domain.replace(/^\./, "").toLowerCase();
  return cookie.hostOnly ? host === domain : host === domain || host.endsWith("." + domain);
}
async function syncCookies() {
  if (!await connection()) return { count: 0, missing: [] };
  const config = await request("/config"), saved = await api.storage.local.get("cookieStoreId");
  const stores = (await api.cookies.getAllCookieStores()).filter(regularStore);
  const store = saved.cookieStoreId ? stores.find(s => s.id === saved.cookieStoreId) : stores.find(s => s.id === "0" || s.id === "firefox-default");
  if (saved.cookieStoreId && !store) throw new Error("The selected cookie store is unavailable. Open the popup and select a regular store.");
  if (!store) throw new Error("No regular cookie store is available.");
  // Never combine private windows, Firefox containers, or different profiles.
  let count = 0, skipped = 0; const missing = [];
  const all = await api.cookies.getAll({ storeId: store.id });
  for (const origin of config.origins) {
    if (!await api.permissions.contains({ origins: permissionsFor([origin]) })) { missing.push(origin); continue; }
    const cookies = all.filter(cookie => matches(cookie, origin)).map(cookie => ({
      name: cookie.name, value: cookie.value, domain: cookie.domain, path: cookie.path,
      secure: cookie.secure, httpOnly: cookie.httpOnly, hostOnly: cookie.hostOnly,
      session: cookie.session, expirationDate: cookie.expirationDate ?? null, sameSite: cookie.sameSite ?? "unspecified",
      partitionKey: cookie.partitionKey ?? (cookie.firstPartyDomain ? { topLevelSite: cookie.firstPartyDomain } : null)
    }));
    const result = await request("/cookies", { origin, cookies }); count += result.accepted; skipped += result.skipped;
  }
  const result = { count, skipped, missing, time: Date.now() };
  await api.storage.local.set({ lastSync: result }); return result;
}
