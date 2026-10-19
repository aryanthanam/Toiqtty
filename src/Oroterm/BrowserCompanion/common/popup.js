const element = id => document.getElementById(id);
let configuration;
function show(error) { element("status").textContent = error instanceof Error ? error.message : error; }
async function refresh() {
  const saved = await api.storage.local.get(["connection", "name", "cookieStoreId"]);
  element("name").value = saved.name ?? "";
  const stores = (await api.cookies.getAllCookieStores()).filter(regularStore);
  element("store").replaceChildren(...stores.map(store => {
    const option = document.createElement("option"); option.value = store.id;
    option.textContent = store.id + (store.id === "0" || store.id === "firefox-default" ? " · Default" : " · Container"); return option;
  }));
  if (saved.cookieStoreId) element("store").value = saved.cookieStoreId;
  if (!element("store").value && stores[0]) element("store").value = stores[0].id;
  await api.storage.local.set({ cookieStoreId: element("store").value });
  if (saved.connection) {
    configuration = await request("/config");
    element("sites").textContent = configuration.origins.length ? "Selected sites: " + configuration.origins.join(", ") : "No sites selected. Add sites in Oroterm → Browser → Profiles and cookie sharing.";
    element("sync").disabled = false;
  }
}
element("connect").addEventListener("click", async () => {
  try {
    const pairing = JSON.parse(atob(element("code").value.trim()));
    const name = element("name").value.trim(); if (!name) throw new Error("Enter a browser and profile name.");
    const result = await request("/pair", { name }, pairing);
    await api.storage.local.set({ connection: { port: pairing.port, token: result.token }, name });
    element("code").value = "";
    await api.alarms.create("oroterm-sync", { periodInMinutes: 1 });
    show("Connected. Select sites in Oroterm, then reopen this popup to approve access."); await refresh();
  } catch (error) { show(error); }
});
element("store").addEventListener("change", () => api.storage.local.set({ cookieStoreId: element("store").value }));
element("sync").addEventListener("click", async () => {
  try {
    // Request immediately in the click handler so the browser sees a user gesture.
    if (!configuration) throw new Error("Reopen this popup to load the selected sites.");
    if (configuration.origins.length && !await api.permissions.request({ origins: permissionsFor(configuration.origins) })) throw new Error("Site access was not granted.");
    const result = await syncCookies();
    show(`Synced ${result.count} cookies.` + (result.skipped ? ` Skipped ${result.skipped} partitioned cookies.` : "") + (result.missing.length ? " Reopen this popup to approve newly selected sites." : ""));
  } catch (error) { show(error); }
});
element("disconnect").addEventListener("click", async () => {
  await api.storage.local.remove(["connection", "lastSync"]);
  const granted = await api.permissions.getAll();
  const origins = (granted.origins ?? []).filter(origin => !origin.startsWith("http://127.0.0.1/"));
  if (origins.length) await api.permissions.remove({ origins });
  configuration = undefined; element("sync").disabled = true; show("Forgot connection. Also disconnect it in Oroterm to revoke its token.");
});
refresh().catch(() => show("Open Oroterm to connect or sync."));
