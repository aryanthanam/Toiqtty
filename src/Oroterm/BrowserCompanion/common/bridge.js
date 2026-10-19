/* No page injection, password access, browser database access, or remote service. */
var api = globalThis.browser ?? globalThis.chrome;
async function connection() { return (await api.storage.local.get("connection")).connection; }
async function request(path, data, supplied) {
  const saved = supplied ?? await connection();
  if (!saved || !Number.isInteger(saved.port) || saved.port < 1 || saved.port > 65535 || !/^[A-F0-9]{64}$/.test(saved.token)) throw new Error("Connect this browser profile first.");
  const response = await fetch(`http://127.0.0.1:${saved.port}${path}`, {
    method: data === undefined ? "GET" : "POST", cache: "no-store", credentials: "omit",
    headers: { "Authorization": `Bearer ${saved.token}`, ...(data === undefined ? {} : { "Content-Type": "application/json" }) },
    body: data === undefined ? undefined : JSON.stringify(data), signal: AbortSignal.timeout(8000)
  });
  if (!response.ok) throw new Error(response.status === 401 ? "Connection revoked or pairing code expired. Reconnect in Oroterm." : "Oroterm rejected this request. Check the configured sites.");
  return response.json();
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
