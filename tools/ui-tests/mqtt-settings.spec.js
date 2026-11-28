// The MQTT card in Settings: off by default, a retained last will is off with its warning, and the password is
// write-only: the page shows only that one is saved, a save without typing one keeps it, "Remove" sends an empty one,
// and Test sends what is on the page (a typed password, never the stored one, which the page does not have).
// The subscriptions are rows of topic, key, JSON field and unit that can be added and removed, up to the agent's cap.
const { test, expect } = require('@playwright/test');

const config = (mqtt) => ({
  display: { language: 'en', theme: 'default' },
  pages: [],
  ...(mqtt ? { mqtt } : {}),
});

async function open(page, cfg) {
  await page.goto('/panel/style.css');
  await page.evaluate((c) => localStorage.setItem('pb-demo-config', JSON.stringify(c)), cfg);
  await page.goto('/settings/#general');
  await page.evaluate(() => {
    const real = window.fetch;
    window.__mqttCalls = [];
    window.__mqttAnswer = { ok: true, errorKind: null };
    window.fetch = async (input, init) => {
      const url = String(input);
      if (url.endsWith('/api/mqtt/test')) {
        window.__mqttCalls.push(JSON.parse(init.body));
        return new Response(JSON.stringify(window.__mqttAnswer), { headers: { 'Content-Type': 'application/json' } });
      }
      if (url.endsWith('/api/mqtt/status')) return new Response(JSON.stringify({ state: 'off', lastMessageAt: null, errorKind: null }), { headers: { 'Content-Type': 'application/json' } });
      return real(input, init);
    };
  });
}

test('it is off by default and nothing is retained unless asked', async ({ page }) => {
  await open(page, config());
  await expect(page.locator('#mqttCard')).toBeVisible();
  await expect(page.locator('[data-bind="mqtt.enabled"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="mqtt.willRetain"]')).not.toBeChecked();
  await expect(page.locator('[data-bind="mqtt.tls"]')).not.toBeChecked();
  // a configuration without the section still shows the defaults
  await expect(page.locator('#mqttTransport')).toHaveValue('tcp');
  await expect(page.locator('#mqttPort')).toHaveValue('1883');
  await expect(page.locator('#mqttKeepAlive')).toHaveValue('60');
  await expect(page.locator('[data-bind="mqtt.cleanSession"]')).toBeChecked();
  await expect(page.locator('#mqttCard')).toContainText('Leave this off unless you are sure');
  // retained messages arrive when a client subscribes; the agent publishes nothing itself, and the last will is the one thing the broker publishes
  await expect(page.locator('#mqttCard')).toContainText('delivered to a device whenever it subscribes');
  await expect(page.locator('#mqttCard')).toContainText('The agent publishes nothing itself; only the last will, if you set one, is published by the broker');
  await expect(page.locator('#mqttCard')).toContainText('the topics you subscribe to'); // the broker sees them
  await expect(page.locator('#mqttCard')).not.toContainText('nothing is subscribed or published');
  await expect(page.locator('#mqttCard')).toContainText('there is no way to switch the check off');
});

test('a saved password is never shown, and typing then emptying the field keeps it', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local', port: 1883, passwordSet: true }));
  await expect(page.locator('#mqttPassword')).toHaveValue('');
  await expect(page.locator('#mqttPassword')).toHaveAttribute('placeholder', '••••••••');
  await expect(page.locator('#mqttPasswordRemove')).toBeVisible();
  await page.locator('#mqttPassword').fill('x');
  expect(await page.evaluate(() => cfg.mqtt.password)).toBe('x');
  await page.locator('#mqttPassword').fill('');
  expect(await page.evaluate(() => 'password' in cfg.mqtt)).toBe(false); // left out: the agent keeps the stored one
});

test('Remove sends an empty password and hides itself', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local', passwordSet: true }));
  await page.locator('#mqttPasswordRemove').click();
  expect(await page.evaluate(() => cfg.mqtt.password)).toBe('');
  await expect(page.locator('#mqttPasswordRemove')).toBeHidden();
  await expect(page.locator('#mqttPassword')).toHaveAttribute('placeholder', '');
});

test('without a password saved there is nothing to remove', async ({ page }) => {
  await open(page, config({ host: 'broker.local' }));
  await expect(page.locator('#mqttPasswordRemove')).toBeHidden();
});

test('Test sends the page\'s settings without the saved-password flag, and shows the kind of error in words', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local', port: 8883, tls: true, username: 'u', passwordSet: true }));
  await page.locator('#mqttTest').click();
  await expect(page.locator('#mqttState')).toHaveText('Connected');
  let sent = await page.evaluate(() => window.__mqttCalls[0]);
  expect(sent.host).toBe('broker.local');
  expect(sent.port).toBe(8883);
  expect(sent.tls).toBe(true);
  expect('password' in sent).toBe(false); // the agent adds the stored one, for this broker only
  expect('passwordSet' in sent).toBe(false);

  await page.evaluate(() => { window.__mqttAnswer = { ok: false, errorKind: 'auth' }; });
  await page.locator('#mqttPassword').fill('typed');
  await page.locator('#mqttTest').click();
  await expect(page.locator('#mqttState')).toHaveText('The broker refused the user name or password.');
  sent = await page.evaluate(() => window.__mqttCalls[1]);
  expect(sent.password).toBe('typed');

  for (const [kind, text] of [['config', 'cannot be used'], ['refused', 'could not be reached'], ['dns', 'could not be reached'], ['timeout', 'could not be reached'], ['tls', 'certificate was not accepted'], ['protocol', 'The connection failed']]) {
    await page.evaluate((k) => { window.__mqttAnswer = { ok: false, errorKind: k }; }, kind);
    await page.locator('#mqttTest').click();
    await expect(page.locator('#mqttState')).toContainText(text);
  }
});

test('Revert brings the password field back to what is saved', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local', passwordSet: true }));
  await page.locator('#mqttPassword').fill('typed-then-reverted');
  await page.locator('#revert').click();
  await expect(page.locator('#mqttPassword')).toHaveValue('');
  expect(await page.evaluate(() => 'password' in cfg.mqtt)).toBe(false);
});

test('the card is translated (German)', async ({ page }) => {
  await open(page, { ...config(), display: { language: 'de', theme: 'default' } });
  await expect(page.locator('#mqttEnabledLabel')).toHaveText('Mit einem MQTT-Broker verbinden');
  await expect(page.locator('#mqttPasswordRemove')).toBeHidden();
  await expect(page.locator('#mqttCard label[data-i18n="s.mqtt.subs"]')).toHaveText('Abonnements');
  await expect(page.locator('#mqttSubAdd')).toHaveText('＋ Abonnement hinzufügen');
});

test('the subscriptions are rows that can be edited, added and removed', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local', subscriptions: [{ topic: 'home/+/temp', key: 'temp', field: 'sensor.temp', unit: '°C' }, { topic: 'door/state', key: 'door' }] }));
  const rows = page.locator('#mqttSubs .mqtt-sub');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0).locator('input').nth(0)).toHaveValue('home/+/temp');
  await expect(rows.nth(0).locator('input').nth(1)).toHaveValue('temp');
  await expect(rows.nth(0).locator('input').nth(2)).toHaveValue('sensor.temp');
  await expect(rows.nth(0).locator('input').nth(3)).toHaveValue('°C');
  await expect(rows.nth(1).locator('input').nth(2)).toHaveValue('');
  await expect(page.locator('#mqttCard')).toContainText('at most once a second');

  await rows.nth(1).locator('input').nth(1).fill('front-door');
  expect(await page.evaluate(() => cfg.mqtt.subscriptions[1].key)).toBe('front-door');

  await page.locator('#mqttSubAdd').click();
  await expect(rows).toHaveCount(3);
  await expect(rows.nth(2).locator('input').nth(0)).toBeFocused();
  await rows.nth(2).locator('input').nth(0).fill('a/#');
  await rows.nth(2).locator('input').nth(1).fill('all');
  expect(await page.evaluate(() => cfg.mqtt.subscriptions[2])).toEqual({ topic: 'a/#', key: 'all', field: '', unit: '' });

  await rows.nth(0).getByRole('button', { name: 'Remove' }).click();
  await expect(rows).toHaveCount(2);
  expect(await page.evaluate(() => cfg.mqtt.subscriptions.map((x) => x.key))).toEqual(['front-door', 'all']);
});

test('a configuration without subscriptions shows none, and there is a room for 32 at most', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local' }));
  const rows = page.locator('#mqttSubs .mqtt-sub');
  await expect(rows).toHaveCount(0);
  for (let i = 0; i < 32; i++) await page.locator('#mqttSubAdd').click();
  await expect(rows).toHaveCount(32);
  await expect(page.locator('#mqttSubAdd')).toBeDisabled();
  await rows.nth(31).getByRole('button', { name: 'Remove' }).click();
  await expect(page.locator('#mqttSubAdd')).toBeEnabled();
});

test('Revert brings the subscriptions back to what is saved', async ({ page }) => {
  await open(page, config({ enabled: true, host: 'broker.local', subscriptions: [{ topic: 'a/b', key: 'ab' }] }));
  await page.locator('#mqttSubAdd').click();
  await expect(page.locator('#mqttSubs .mqtt-sub')).toHaveCount(2);
  await page.locator('#revert').click();
  await expect(page.locator('#mqttSubs .mqtt-sub')).toHaveCount(1);
});
