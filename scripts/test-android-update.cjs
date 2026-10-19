const fs = require('fs');
const vm = require('vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync('companion/android/app/src/main/java/universe/dia/conphone/UpdateActivity.kt', 'utf8');
const script = source.split('web.evaluateJavascript("""')[1].split('""".trimIndent()')[0].replaceAll("${'$'}", '$');
async function check(pathname, anchor, asset, expectedTag) {
  let fetched;
  const context = {
    URL, window: {}, location: { origin: 'https://github.com', pathname, href: 'https://github.com' + pathname },
    document: { querySelector: () => anchor && ({ getAttribute: () => anchor }) },
    fetch: async url => { fetched = url; return { ok: true, text: async () => 'fixture' }; },
    DOMParser: class { parseFromString() { return { querySelectorAll: () => asset ? [{getAttribute: () => asset}] : [] }; } }
  };
  vm.runInNewContext(script, context);
  await new Promise(resolve => setImmediate(resolve));
  if (expectedTag) {
    assert.equal(fetched, 'https://github.com/DIA-Universe/Con.Phone/releases/expanded_assets/' + expectedTag);
    assert.equal(context.window.__conphoneUpdate.url, 'https://github.com' + asset);
  } else assert.ok(context.window.__conphoneUpdate.error);
}
(async () => {
  const base = '/DIA-Universe/Con.Phone/releases';
  await check(base+'/tag/v1.0.2', base+'/tag/v0.3.0', base+'/download/v1.0.2/LINKO-1.0.2.apk', 'v1.0.2');
  await check(base, base+'/tag/v1.0.2', base+'/download/v1.0.2/ConPhone-Companion-1.0.2-debug.apk', 'v1.0.2');
  await check(base, null, null, null);
  await check(base+'/tag/v1.0.2', null, null, null);
  console.log('PASS: actual updater JS selects redirected tag, handles legacy asset and missing release/APK.');
})().catch(error => { console.error(error); process.exitCode = 1; });
