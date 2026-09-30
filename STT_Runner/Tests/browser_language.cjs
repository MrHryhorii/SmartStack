// Exercise saved preferences against the production language-loading handler.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../wwwroot/app.js'), 'utf8')
  .replace('void initialize();', '');

async function check(saved, expected) {
  const storage = new Map(saved ? [['mwandishi-language', saved]] : []);
  const nodes = new Map();
  const language = {
    options: [{ value: 'auto' }], selected: 'auto',
    get value() { return this.selected; },
    set value(value) { this.selected = this.options.some(option => option.value === value) ? value : ''; },
    replaceChildren() { this.options = []; this.selected = ''; },
    add(option) { this.options.push(option); },
    addEventListener() {}
  };
  const context = vm.createContext({
    document: {
      querySelector(selector) {
        if (selector === '#languageSelect') return language;
        if (!nodes.has(selector)) nodes.set(selector, {
          addEventListener() {}, classList: { toggle() {} }
        });
        return nodes.get(selector);
      }
    },
    localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value)
    },
    Option: function(name, value) { this.textContent = name; this.value = value; },
    fetch: async url => ({ ok: true, json: async () => url === '/health'
      ? { model_family: 'small', backend: 'Cpu' }
      : { data: [{ name: 'Auto detect', code: 'auto' }, { name: 'Ukrainian', code: 'uk' }, { name: 'English', code: 'en' }] } })
  });
  vm.runInContext(source, context);
  await vm.runInContext('checkServer()', context);
  assert.equal(language.value, expected);
}
(async () => {
  await check('uk', 'uk');
  await check('en', 'en');
  await check(null, 'auto');
  await check('unsupported', 'auto');
  console.log('PASS: remembered language restored; missing and unsupported choices use auto');
})().catch(error => { console.error(error); process.exitCode = 1; });
