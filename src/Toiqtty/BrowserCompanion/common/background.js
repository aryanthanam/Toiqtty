if (typeof importScripts === "function") importScripts("bridge.js");
api.runtime.onInstalled.addListener(() => api.alarms.create("toiqtty-sync", { periodInMinutes: 1 }));
api.runtime.onStartup.addListener(() => api.alarms.create("toiqtty-sync", { periodInMinutes: 1 }));
api.alarms.onAlarm.addListener(alarm => { if (alarm.name === "toiqtty-sync") syncCookies().catch(() => {}); });
let changed;
api.cookies.onChanged.addListener(() => {
  clearTimeout(changed);
  changed = setTimeout(() => syncCookies().catch(() => {}), 1500);
});
