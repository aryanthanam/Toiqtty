const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const common = path.join(__dirname, '../src/Oroterm/BrowserCompanion/common');
function fixture() {
  const sent = [], queried = [], state = { connection: { port: 12345, token: 'A'.repeat(64) }, cookieStoreId: 'firefox-container-2' };
  const cookie = (name, domain, hostOnly = false, extra = {}) => ({ name, value: name, domain, hostOnly, path: '/', secure: true, httpOnly: true, session: false, expirationDate: 2000000000, sameSite: 'strict', ...extra });
  const cookies = [cookie('parent', '.example.com'), cookie('host', 'app.example.com', true), cookie('unrelated', 'evil.test', true), cookie('wrong-host', 'example.com', true), cookie('partitioned', '.example.com', false, { partitionKey: { topLevelSite: 'https://example.com' } })];
  const stores = [{ id: 'firefox-default' }, { id: 'firefox-container-2' }];
  const context = vm.createContext({ URL, AbortSignal, Error, fetch: async (url, options) => {
    assert.equal(new URL(url).hostname, '127.0.0.1'); assert.equal(options.credentials, 'omit');
    if (url.endsWith('/config')) return { ok: true, json: async () => ({ origins: ['https://app.example.com', 'https://denied.example.com'] }) };
    sent.push(JSON.parse(options.body)); return { ok: true, json: async () => ({ accepted: 2, skipped: 1 }) };
  }, chrome: {
    storage: { local: { get: async () => state, set: async values => Object.assign(state, values) } },
    permissions: { contains: async ({ origins }) => !origins[0].includes('denied') },
    cookies: { getAllCookieStores: async () => stores, getAll: async details => { queried.push(details); return cookies; } }
  } });
  vm.runInContext(fs.readFileSync(path.join(common, 'bridge.js'), 'utf8'), context);
  return { context, sent, queried, state, cookies, stores };
}
test('companion requests only selected-site permissions and uses the loopback bridge', async () => {
  const f = fixture(); const result = await vm.runInContext('syncCookies()', f.context);
  assert.deepEqual(Array.from(result.missing), ['https://denied.example.com']);
  assert.equal(f.sent.length, 1); assert.equal(f.sent[0].origin, 'https://app.example.com');
  assert.equal(f.queried[0].storeId, 'firefox-container-2');
});
test('snapshots retain cookie attributes, respect host-only scope, and retain partition markers', async () => {
  const f = fixture(); await vm.runInContext('syncCookies()', f.context);
  assert.deepEqual(f.sent[0].cookies.map(c => c.name), ['parent', 'host', 'partitioned']);
  assert.equal(f.sent[0].cookies[1].httpOnly, true); assert.equal(f.sent[0].cookies[1].sameSite, 'strict');
  assert.equal(f.sent[0].cookies[2].partitionKey.topLevelSite, 'https://example.com');
});
test('empty source stores produce deletion snapshots', async () => {
  const f = fixture(); f.cookies.length = 0; await vm.runInContext('syncCookies()', f.context);
  assert.equal(f.sent[0].cookies.length, 0);
});
test('malformed connection settings cannot redirect cookie values to another host', async () => {
  const f = fixture(); f.state.connection.port = '12345/../../evil';
  await assert.rejects(vm.runInContext('syncCookies()', f.context)); assert.equal(f.sent.length, 0);
});
test('private stores are excluded even when Chromium omits the incognito property', async () => {
  const f = fixture(); f.stores.splice(0, f.stores.length, { id: '1' }, { id: 'firefox-private' }, { id: '0' });
  delete f.state.cookieStoreId; await vm.runInContext('syncCookies()', f.context);
  assert.equal(f.queried[0].storeId, '0');
  f.state.cookieStoreId = '1'; await assert.rejects(vm.runInContext('syncCookies()', f.context));
});
test('a missing container never silently falls back to another cookie store', async () => {
  const f = fixture(); f.state.cookieStoreId = 'firefox-container-missing';
  await assert.rejects(vm.runInContext('syncCookies()', f.context)); assert.equal(f.queried.length, 0);
});
test('localhost and IPv6 site permissions use portable patterns without explicit ports', () => {
  const f = fixture();
  const patterns = vm.runInContext('permissionsFor(["http://localhost:3000", "http://localhost:8080", "http://[::1]:5000"])', f.context);
  assert.deepEqual(Array.from(patterns), ['http://localhost/*', 'http://[::1]/*']);
});
test('Chromium and Firefox manifests have no page injection or default access to remote sites', () => {
  for (const browser of ['chromium', 'firefox']) {
    const manifest = JSON.parse(fs.readFileSync(path.join(common, '..', browser, 'manifest.json'), 'utf8'));
    assert.deepEqual(manifest.host_permissions, ['http://127.0.0.1/*']);
    assert.deepEqual(manifest.permissions, ['cookies', 'storage', 'alarms']);
    assert.equal(manifest.content_scripts, undefined);
    assert.ok(manifest.optional_host_permissions.includes('https://*/*'));
  }
});
