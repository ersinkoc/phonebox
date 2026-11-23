const { test, expect } = require('@playwright/test');

// Discord widget: the demo answers /api/discord with you in a channel where Ada starts and stops speaking every 1.5 s.

const DISCORD = { id: 'd', type: 'discord', x: 0, y: 0, w: 6, h: 5, options: { control: true } };
const ONE_PAGE = [{ id: 'dc', title: 'Discord', enabled: true, widgets: [DISCORD] }];

async function show(page, { counting = false, pages = ONE_PAGE } = {}) {
  if (counting) {
    await page.addInitScript(() => {
      window.__dcRequests = 0;
      const wrap = () => {
        const f = window.fetch;
        if (!f || f.__dc) return;
        const g = (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.endsWith('/api/discord')) window.__dcRequests++;
          return f(input, init);
        };
        g.__dc = true;
        window.fetch = g;
      };
      setInterval(wrap, 10);
    });
  }
  await page.goto('/panel/');
  await page.evaluate((pages) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], pages }));
  }, pages);
  await page.reload();
}

test('a member who is speaking gets the ring, and loses it when they stop', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  const ada = page.locator('.dc-member', { hasText: 'Ada' });
  await expect(ada).toBeVisible();
  await expect(ada).toHaveClass(/speaking/, { timeout: 5000 });
  await expect(ada).not.toHaveClass(/speaking/, { timeout: 5000 });
  await expect(page.locator('.dc-member', { hasText: 'Linus' })).not.toHaveClass(/speaking/);
  expect(errors).toEqual([]);
});

test('the widget asks more often only while you are in a channel', async ({ page }) => {
  await show(page, { counting: true });
  await expect(page.locator('.dc-member').first()).toBeVisible();
  // in a channel: about every 500 ms (some 8 in 4 s; the bounds leave room for a busy test machine)
  let start = await page.evaluate(() => window.__dcRequests);
  await page.waitForTimeout(4000);
  expect(await page.evaluate(() => window.__dcRequests) - start).toBeGreaterThanOrEqual(5);
  // out of the channel: back to every 2 s
  await page.locator('.dc-btn.leave').click();
  await expect(page.locator('.dc-member')).toHaveCount(0);
  start = await page.evaluate(() => window.__dcRequests);
  await page.waitForTimeout(4000);
  expect(await page.evaluate(() => window.__dcRequests) - start).toBeLessThanOrEqual(3);
});

const requestsIn = async (page, ms) => {
  const start = await page.evaluate(() => window.__dcRequests);
  await page.waitForTimeout(ms);
  return await page.evaluate(() => window.__dcRequests) - start;
};

test('a widget on a neighbouring page does not ask until its page is shown', async ({ page }) => {
  // the panel keeps the pages next to the visible one up to date, so this one gets every update while off screen
  await show(page, { counting: true, pages: [
    { id: 'home', title: 'Home', enabled: true, widgets: [{ id: 'n', type: 'note', x: 0, y: 0, w: 6, h: 5, options: { text: 'Home' } }] },
    { id: 'dc', title: 'Discord', enabled: true, widgets: [DISCORD] },
  ] });
  await expect(page.locator('#dots i')).toHaveCount(2);
  expect(await requestsIn(page, 3000)).toBe(0);
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#dots i').nth(1)).toHaveClass('on');
  await expect(page.locator('.dc-member', { hasText: 'Ada' })).toBeVisible();
  expect(await requestsIn(page, 3000)).toBeGreaterThanOrEqual(4);
});

test('a Discord card that is not showing in a Smart Stack does not ask', async ({ page }) => {
  await show(page, { counting: true, pages: [{ id: 'st', title: 'Stack', enabled: true, widgets: [{ id: 's', type: 'stack', x: 0, y: 0, w: 6, h: 5,
    options: { interval: 0, widgets: [{ id: 'n', type: 'note', options: { text: 'Front' } }, { id: 'd', type: 'discord', options: { control: true } }] } }] }] });
  await expect(page.locator('.stack-item.active .w-note')).toBeVisible();
  expect(await requestsIn(page, 3000)).toBe(0);
});

test('the input mode button switches between voice activity and push to talk', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  const mode = page.locator('.dc-btn.mode');
  await expect(mode).toBeVisible();
  await expect(mode).not.toHaveClass(/\bon\b/);
  await expect(mode).toHaveAttribute('aria-label', 'Switch to push to talk');
  await mode.click();
  await expect(mode).toHaveClass(/\bon\b/);
  await expect(mode).toHaveAttribute('aria-label', 'Switch to voice activity');
  await expect(page.locator('.dc-warn')).toHaveCount(0); // the demo has a push-to-talk key
  await mode.click();
  await expect(mode).not.toHaveClass(/\bon\b/);
  expect(errors).toEqual([]);
});

test('push to talk without a key in Discord shows a warning', async ({ page }) => {
  // the agent reports pttKey: false when Discord has no push-to-talk key; the demo always has one, so drop it here
  await page.addInitScript(() => {
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__nokey) return;
      const g = async (input, init) => {
        const r = await f(input, init);
        const url = typeof input === 'string' ? input : input.url;
        if (!url.endsWith('/api/discord')) return r;
        const j = await r.clone().json();
        if (j.snapshot) j.snapshot.pttKey = false;
        return new Response(JSON.stringify(j), { headers: { 'Content-Type': 'application/json' } });
      };
      g.__nokey = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  });
  await show(page);
  await expect(page.locator('.dc-warn')).toHaveCount(0);
  await page.locator('.dc-btn.mode').click();
  await expect(page.locator('.dc-warn')).toHaveText(/no key is set in Discord/);
  await page.locator('.dc-btn.mode').click();
  await expect(page.locator('.dc-warn')).toHaveCount(0);
});

async function showWith(page, discord, widget) {
  await page.goto('/panel/');
  await page.evaluate(([discord, widget]) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], discord,
      pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [widget] }] }));
  }, [discord, widget]);
  await page.reload();
}

test('without the widget option the buttons stay off, unless Allow Discord control is on', async ({ page }) => {
  const plain = { ...DISCORD, options: {} };
  await showWith(page, { allowControl: false }, plain);
  await expect(page.locator('.dc-btn.mute')).toBeDisabled();
  await showWith(page, { allowControl: true }, plain);
  await expect(page.locator('.dc-btn.mute')).toBeEnabled();
  await page.locator('.dc-btn.mute').click();
  await expect(page.locator('.dc-btn.mute')).toHaveClass(/\bon\b/);
});

test('a voice channel shows who is in it, and the server how many', async ({ page }) => {
  const withFavs = (options) => [{ ...ONE_PAGE[0], widgets: [{ ...DISCORD, options: { control: true, ...options } }] }];
  await show(page, { pages: withFavs({ favGuilds: ['g1'] }) });
  // the server you are in starts unfolded; the agent reports people for favourites only
  const lounge = page.locator('.dc-room', { hasText: 'Lounge' });
  await expect(lounge.locator('.dc-person', { hasText: 'Grace' })).toBeVisible();
  await expect(lounge.locator('.dc-person.muted', { hasText: 'Alan' })).toBeVisible();
  await expect(page.locator('.dc-server', { hasText: 'Friends' }).locator('.dc-invoice')).toHaveText(/2/);
  await show(page, { pages: withFavs({}) });
  await expect(page.locator('.dc-room', { hasText: 'Lounge' })).toBeVisible();
  await expect(page.locator('.dc-person')).toHaveCount(0);
});

// answers the favourite write with a status, after a delay, and counts how many were sent
async function favAnswers(page, status, delay = 0) {
  await page.addInitScript(([status, delay]) => {
    window.__favs = 0;
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__fav) return;
      const g = async (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (!url.includes('/discord-favorite')) return f(input, init);
        window.__favs++;
        await new Promise((done) => setTimeout(done, delay));
        return status === 200 ? f(input, init) : new Response(JSON.stringify({ error: 'changed' }), { status, headers: { 'Content-Type': 'application/json' } });
      };
      g.__fav = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  }, [status, delay]);
}

test('a favourite saved over newer settings says so, and keeps the star as it was', async ({ page }) => {
  await favAnswers(page, 409);
  await show(page);
  await page.locator('.dc-favbtn').click();
  const star = page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star');
  await star.click();
  await expect(page.getByText('The settings were changed on the computer, try again')).toBeVisible();
  await expect(star).not.toHaveClass(/on/);
  await expect(star).toBeEnabled();
});

test('settings changed on the computer meanwhile are not written over', async ({ page }) => {
  await show(page);
  await page.locator('.dc-favbtn').click();
  const star = page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star');
  await expect(star).toBeVisible();
  // another save (the settings page): the panel has not read it yet when the star is tapped
  await page.evaluate(() => {
    localStorage.setItem('pb-demo-version', JSON.stringify(JSON.parse(localStorage.getItem('pb-demo-version') || '1') + 1));
    [...document.querySelectorAll('.dc-block')].find((b) => b.textContent.includes('Work')).querySelector('.dc-headrow > .dc-star').click();
  });
  await expect(page.getByText('The settings were changed on the computer, try again')).toBeVisible();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-demo-config')).pages[0].widgets[0].options.favGuilds)).toBeUndefined();
});

test('a favourite is saved after a settings read that overtook the save before it', async ({ page }) => {
  // the settings answer held back from before the save is applied while the save runs, so the panel holds the old version
  await page.addInitScript(() => {
    window.__statuses = [];
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__overtake) return;
      const g = async (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (!url.includes('/discord-favorite')) return f(input, init);
        if (!window.__overtaken) {
          window.__overtaken = true;
          await window.__loadConfig();
        }
        const r = await f(input, init);
        window.__statuses.push(r.status);
        return r;
      };
      g.__overtake = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  });
  await show(page);
  await page.evaluate(async () => { const m = await import(document.querySelector('script[type=module]').src); window.__loadConfig = m.loadConfig; });
  await page.locator('.dc-favbtn').click();
  // tap the next star the moment the save lets go of it: the panel's regular refresh would bring the new version later
  await page.evaluate(async () => {
    const room = () => [...document.querySelectorAll('.dc-room')].find((r) => r.textContent.includes('Lounge'))?.querySelector('.dc-star');
    [...document.querySelectorAll('.dc-block')].find((b) => b.textContent.includes('Work')).querySelector('.dc-headrow > .dc-star').click();
    const until = Date.now() + 8000;
    while (Date.now() < until && (!window.__statuses.length || room()?.disabled)) await new Promise((r) => setTimeout(r, 5));
    if (!room()) document.querySelector('.dc-favbtn').click(); // the settings read again redrew the widget, out of its edit mode
    room().click();
  });
  await expect.poll(() => page.evaluate(() => window.__statuses)).toEqual([200, 200]);
});

test('a second tap while a favourite is being saved is ignored', async ({ page }) => {
  await favAnswers(page, 200, 1200);
  await show(page);
  await page.locator('.dc-favbtn').click();
  const star = page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star');
  await star.click();
  await expect(star).toBeDisabled();
  await star.evaluate((b) => b.click());
  await page.locator('.dc-room', { hasText: 'Lounge' }).locator('.dc-star').evaluate((b) => b.click());
  await expect(star).toHaveClass(/on/);
  await expect(star).toBeEnabled();
  expect(await page.evaluate(() => window.__favs)).toBe(1);
});

test('favourite servers and channels are picked on the phone and kept', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await show(page);
  await expect(page.locator('.dc-server', { hasText: 'Work' })).toBeVisible();
  await page.locator('.dc-favbtn').click();
  // star the Work server
  await page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star').click();
  await expect(page.locator('.dc-block', { hasText: 'Work' }).locator('.dc-headrow > .dc-star')).toHaveClass(/on/);
  // star one channel of the server you are in: that server then shows only it and the channel you are in
  await page.locator('.dc-room', { hasText: 'Lounge' }).locator('.dc-star').click();
  await page.locator('.dc-favbtn').click(); // Done
  await expect(page.locator('.dc-server', { hasText: 'Work' })).toBeVisible();
  await expect(page.locator('.dc-room')).toHaveCount(2); // Game night (you are in it) and Lounge
  // saved on the computer: still so after the panel reloads
  await page.reload();
  await expect(page.locator('.dc-room', { hasText: 'Lounge' })).toBeVisible();
  await expect(page.locator('.dc-room')).toHaveCount(2);
  expect(errors).toEqual([]);
});

test('favourites Discord no longer lists hide nothing, and can be removed', async ({ page }) => {
  await show(page, { pages: [{ ...ONE_PAGE[0], widgets: [{ ...DISCORD, options: { control: true, favGuilds: ['g9', 5, null, 'g9'] } }] }] });
  await expect(page.locator('.dc-server')).toHaveCount(2);
  await page.locator('.dc-favbtn').click();
  await expect(page.locator('.dc-goneid')).toHaveText(["g9No longer in Discord's list"]);
  await page.locator('.dc-gone .dc-star').click();
  await expect(page.locator('.dc-gone')).toHaveCount(0);
});

test('without favourites every server shows', async ({ page }) => {
  await show(page);
  await expect(page.locator('.dc-server')).toHaveCount(2);
  await expect(page.locator('.dc-room')).toHaveCount(2); // Friends is unfolded: Game night and Lounge
});

// screen share (discord.screenShare in the settings); answers: url -> what the demo's answer is replaced with
async function showShare(page, { screenShare = true, options = { control: true }, answers = [], allowControl = false } = {}) {
  await page.addInitScript((answers) => {
    window.__share = [];
    const wrap = () => {
      const f = window.fetch;
      if (!f || f.__share) return;
      const g = async (input, init) => {
        const url = typeof input === 'string' ? input : input.url;
        if (!url.includes('/discord/share')) return f(input, init);
        window.__share.push(url.replace(/^.*\/discord/, ''));
        const patch = answers.find((x) => new RegExp(x.url).test(url));
        if (patch?.delay) await new Promise((done) => setTimeout(done, patch.delay));
        const r = await f(input, init);
        return patch?.json ? new Response(JSON.stringify({ ...(await r.json()), ...patch.json }), { status: patch.status || 200, headers: { 'Content-Type': 'application/json' } }) : r;
      };
      g.__share = true;
      window.fetch = g;
    };
    setInterval(wrap, 10);
  }, answers);
  await page.goto('/panel/');
  await page.evaluate(([screenShare, options, allowControl]) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], discord: { screenShare, allowControl },
      pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w: 6, h: 5, options }] }] }));
  }, [screenShare, options, allowControl]);
  await page.reload();
  await expect(page.locator('.w-discord .dc-top')).toBeVisible();
}

test('screen share shows only with its setting and control on', async ({ page }) => {
  await showShare(page, { screenShare: false });
  await expect(page.locator('.dc-btn.share')).toHaveCount(0);
  await showShare(page, { options: {} });
  await expect(page.locator('.dc-btn.share')).toHaveCount(0);
  expect(await page.evaluate(() => window.__share)).toEqual([]);
});

test('screen share needs the widget\'s control, not only the global switch', async ({ page }) => {
  await showShare(page, { options: {}, allowControl: true });
  await expect(page.locator('.dc-btn.mute')).toBeEnabled();                 // the global switch allows the voice actions
  await expect(page.locator('.dc-btn.share')).toHaveCount(0);
});

test('an app is shared and stopped, and the apps are asked for only while the view is open', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await showShare(page);
  const button = page.locator('.dc-btn.share');
  await expect(button).toBeVisible();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => window.__share)).toEqual([]);
  await button.click();
  await expect(page.locator('.dc-app')).toHaveText(['Space Racers', 'Firefox', 'Visual Studio Code']);
  expect(await page.evaluate(() => window.__share[0])).toBe('/share?apps=true');
  await page.locator('.dc-app', { hasText: 'Space Racers' }).click();
  await expect(page.locator('.dc-sharing')).toHaveText('Sharing Space Racers');
  await expect(page.locator('.dc-app')).toHaveCount(0);
  await page.locator('.dc-share.stop').click();
  await expect(page.locator('.dc-app')).toHaveCount(3);
  await page.locator('.dc-share').click();                                  // Choose on PC
  await expect(page.locator('.dc-share.stop')).toBeVisible();
  await button.click();                                                      // back to the channels
  await expect(page.locator('.dc-members')).toBeVisible();
  expect(errors).toEqual([]);
});

test('an app Discord cannot share directly says so in the panel language', async ({ page }) => {
  await showShare(page);
  await page.locator('.dc-btn.share').click();
  await page.locator('.dc-app', { hasText: 'Firefox' }).click();
  await expect(page.getByText("Discord can't share this app directly: use Choose on PC")).toBeVisible();
  await expect(page.locator('.dc-app', { hasText: 'Firefox' })).toBeEnabled();
});

test('outside a call, and before Discord has said whether you share, nothing can be started', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share\\?', json: { inCall: false } }] });
  await page.locator('.dc-btn.share').click();
  await expect(page.locator('.dc-hint').first()).toHaveText('Join a voice channel to share');
  await expect(page.locator('.dc-share')).toBeDisabled();
  await expect(page.locator('.dc-app').first()).toBeDisabled();
});

test('a share without a name still offers Stop', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share\\?', json: { active: true, name: null } }] });
  await page.locator('.dc-btn.share').click();
  await expect(page.locator('.dc-sharing')).toHaveText('You are sharing');
  await expect(page.locator('.dc-share.stop')).toBeEnabled();
});

test('an unknown share state offers no buttons', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share\\?', json: { active: null } }] });
  await page.locator('.dc-btn.share').click();
  await expect(page.locator('.w-discord .empty')).toHaveText("Discord hasn't said yet whether you are sharing");
  await expect(page.locator('.dc-share, .dc-app')).toHaveCount(0);
});

test('a declined permission says so in the share view, and the buttons keep working', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share\\?', json: { ready: false, error: null } }] });
  await page.locator('.dc-btn.share').click();
  await expect(page.locator('.w-discord .empty')).toHaveText('Approve PhoneBox in Discord to share your screen');
  await page.locator('.dc-btn.mute').click();
  await expect(page.locator('.dc-btn.mute')).toHaveClass(/\bon\b/);
});

test('closing the view while an action runs does not ask for the apps afterwards', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share/app', delay: 1500 }] });
  await page.locator('.dc-btn.share').click();
  await page.locator('.dc-app', { hasText: 'Space Racers' }).click();
  await page.locator('.dc-btn.share').click();
  await page.evaluate(() => { window.__share.length = 0; });
  await page.waitForTimeout(3000);
  expect(await page.evaluate(() => window.__share)).toEqual([]);
});

test('an action that ends while the panel is off screen does not ask for the apps', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share/app', delay: 1500 }] });
  await page.locator('.dc-btn.share').click();
  await page.locator('.dc-app', { hasText: 'Space Racers' }).click();
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    window.__share.length = 0;
  });
  await page.waitForTimeout(3000);
  expect(await page.evaluate(() => window.__share)).toEqual([]);
});

test('an action that is never answered frees the buttons again', async ({ page }) => {
  await showShare(page, { answers: [{ url: 'share/pick', delay: 60000 }] });
  await page.locator('.dc-btn.share').click();
  await page.locator('.dc-share').click();
  await expect(page.locator('.dc-share')).toBeDisabled();
  await expect(page.locator('.dc-share')).toBeEnabled({ timeout: 15000 });
  expect((await page.evaluate(() => window.__share)).filter((u) => u.startsWith('/share/pick'))).toHaveLength(1); // not sent again by itself
});

// the chat (discord.chat in the settings); answers: url part -> a function of the demo's answer, for what the demo doesn't do
async function showChat(page, { chat = true, options = { control: true }, answers = null, allowControl = false, screenShare = false } = {}) {
  if (answers) {
    await page.addInitScript((answers) => {
      window.__chat = [];
      const wrap = () => {
        const f = window.fetch;
        if (!f || f.__chat) return;
        const g = async (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.includes('/discord/chat')) window.__chat.push(url.replace(/^.*\/discord/, ''));
          const r = await f(input, init);
          const patch = answers.find((x) => new RegExp(x.url).test(url));
          if (!patch) return r;
          const text = JSON.stringify({ ...(await r.json()), ...patch.json });
          if (patch.hold) {
            await new Promise((done) => { window.__release = done; });
            setTimeout(() => { window.__released = true; }, 50); // after the widget has handled it
          }
          return new Response(text, { headers: { 'Content-Type': 'application/json' } });
        };
        g.__chat = true;
        window.fetch = g;
      };
      setInterval(wrap, 10);
    }, answers);
  }
  await page.goto('/panel/');
  await page.evaluate(([chat, options, allowControl, screenShare]) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], discord: { chat, allowControl, screenShare },
      pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w: 6, h: 5, options }] }] }));
  }, [chat, options, allowControl, screenShare]);
  await page.reload();
  await expect(page.locator('.w-discord .dc-top')).toBeVisible();
}

test('the chat and the share view take turns: opening one closes the other', async ({ page }) => {
  await showChat(page, { screenShare: true });
  const chat = page.locator('.dc-btn.chat'), share = page.locator('.dc-btn.share');
  await chat.click();
  await expect(page.locator('.dc-msg').first()).toBeVisible();
  await share.click();
  await expect(share).toHaveClass(/\bon\b/);
  await expect(chat).not.toHaveClass(/\bon\b/); // a chat hidden under the share view would still be read and marked seen
  await expect(page.locator('.dc-msg')).toHaveCount(0);
  await chat.click();
  await expect(share).not.toHaveClass(/\bon\b/);
  await expect(page.locator('.dc-msg').first()).toBeVisible();
});

test('without the chat setting there is no chat', async ({ page }) => {
  await showChat(page, { chat: false });
  await expect(page.locator('.dc-btn.chat')).toHaveCount(0);
});

test('the chat shows the voice channel, then a text channel that is picked', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await showChat(page);
  const chat = page.locator('.dc-btn.chat');
  await expect(chat.locator('.dc-dot')).toBeVisible();                    // a message not seen yet
  await chat.click();
  await expect(page.locator('.dc-msg')).toHaveText([/Ada.*hi from the call/]);
  await expect(page.locator('.dc-members')).toHaveCount(0);               // the chat takes the widget's place
  await page.locator('.dc-pick').click();
  await page.locator('.dc-pickrow.srv', { hasText: 'Friends' }).click();
  await page.locator('.dc-pickrow', { hasText: 'general' }).click();
  await expect(page.locator('.dc-pick')).toContainText('general');
  await expect(page.locator('.dc-msg')).toHaveText([/Ada.*are you joining/, /Linus.*in 5 min/]);
  await chat.click();                                                     // back to the channels
  await expect(page.locator('.dc-members')).toBeVisible();
  await expect(chat.locator('.dc-dot')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('an open channel picker asks for no messages, and goes back to them when it closes', async ({ page }) => {
  await showChat(page, { answers: [] });
  await page.locator('.dc-btn.chat').click();
  await expect(page.locator('.dc-msg')).toHaveCount(1);
  await page.locator('.dc-pick').click();
  await expect(page.locator('.dc-pickrow.srv')).toBeVisible();
  await page.evaluate(() => { window.__chat.length = 0; });
  await expect.poll(() => page.evaluate(() => window.__chat.length), { timeout: 6000 }).toBeGreaterThan(0);
  expect((await page.evaluate(() => window.__chat)).every((u) => u.startsWith('/chat?picker=true'))).toBe(true);
  await page.locator('.dc-pick').click();
  await expect(page.locator('.dc-msg')).toHaveCount(1);
});

test('an answer for a server already left does not replace the newer one', async ({ page }) => {
  const other = { servers: [{ id: 'g1', name: 'Friends', icon: null, unread: false }, { id: 'g2', name: 'Work', icon: null, unread: false }] };
  await showChat(page, { answers: [
    { url: 'guild=g1', hold: true, json: { ...other, channels: [{ id: 't1', name: 'general', unread: false }] } },
    { url: 'guild=g2', json: { ...other, channels: [{ id: 't9', name: 'deploys', unread: false }] } },
    { url: 'discord/chat$|picker=true$', json: other }] });
  await page.locator('.dc-btn.chat').click();
  await page.locator('.dc-pick').click();
  await page.locator('.dc-pickrow.srv', { hasText: 'Friends' }).click();   // its answer is held back
  await expect.poll(() => page.evaluate(() => typeof window.__release)).toBe('function');
  await page.locator('.dc-pickrow.back').click();
  await page.locator('.dc-pickrow.srv', { hasText: 'Work' }).click();
  await expect(page.locator('.dc-pickrow', { hasText: 'deploys' })).toBeVisible();
  await page.evaluate(() => {                                              // now the older one arrives; the next poll would hide it again
    const shown = () => [...document.querySelectorAll('.dc-pickrow')].some((r) => r.textContent.includes('general'));
    window.__stale = false;
    new MutationObserver(() => { window.__stale ||= shown(); }).observe(document.body, { childList: true, subtree: true });
    window.__release();
  });
  await page.waitForFunction(() => window.__released);
  expect(await page.evaluate(() => window.__stale)).toBe(false);
  await expect(page.locator('.dc-pickrow', { hasText: 'deploys' })).toBeVisible();
  await expect(page.locator('.dc-pickrow', { hasText: 'general' })).toHaveCount(0);
});

test('a declined permission says so in the chat, and the buttons keep working', async ({ page }) => {
  await showChat(page, { answers: [{ url: 'discord/chat', json: { ready: false, error: null } }] });
  await page.locator('.dc-btn.chat').click();
  await expect(page.locator('.w-discord .empty')).toHaveText('Approve PhoneBox in Discord to see the chat');
  await page.locator('.dc-btn.mute').click();
  await expect(page.locator('.dc-btn.mute')).toHaveClass(/\bon\b/);
});

test('opening the chat in Discord needs the widget\'s control, not only the global switch', async ({ page }) => {
  await showChat(page);
  await page.locator('.dc-btn.chat').click();
  await expect(page.locator('.dc-open')).toBeVisible();
  await showChat(page, { options: {}, allowControl: true });
  await expect(page.locator('.dc-btn.mute')).toBeEnabled();               // the global switch allows the voice actions
  await page.locator('.dc-btn.chat').click();
  await expect(page.locator('.dc-msg')).toHaveCount(1);
  await expect(page.locator('.dc-open')).toHaveCount(0);
});

test('message text is shown as text, in full, and opening it in Discord needs control', async ({ page }) => {
  const long = '<b>bold</b> ' + 'line\n'.repeat(30);
  const message = { id: '1', author: 'Ada', authorId: '1', avatar: null, color: null, bot: false, text: long, ts: Date.now(), attachments: 2, embed: null };
  await showChat(page, { options: {}, answers: [{ url: 'discord/chat$', json: { messages: [message] } }] });
  await page.locator('.dc-btn.chat').click();
  const text = page.locator('.dc-msgtext');
  await expect(text).toContainText('<b>bold</b>');
  expect(await text.evaluate((e) => e.scrollHeight <= e.clientHeight + 1)).toBe(true); // no scroll area of its own
  await expect(page.locator('.dc-extra')).toHaveText('📎 2');
  await expect(page.locator('.dc-open')).toHaveCount(0);
});

test('the ring moves without redrawing the widget, so avatars and server icons do not flash', async ({ page }) => {
  await show(page);
  const ada = page.locator('.dc-member', { hasText: 'Ada' });
  await expect(ada).toBeVisible();
  await page.locator('.w-discord .dc-ico').evaluateAll((icons) => icons.forEach((i) => { i.__kept = true; }));
  await expect(ada).toHaveClass(/speaking/, { timeout: 5000 });
  await expect(ada).not.toHaveClass(/speaking/, { timeout: 5000 });
  expect(await page.locator('.w-discord .dc-ico').evaluateAll((icons) => icons.every((i) => i.__kept === true))).toBe(true);
});

test('a large widget has larger buttons, names and avatars; a usual one keeps its sizes (#420)', async ({ page }) => {
  const size = async (w, h) => {
    await page.goto('/panel/');
    await page.evaluate(([w, h]) => {
      localStorage.clear();
      localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [],
        pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w, h, options: { control: true } }] }] }));
    }, [w, h]);
    await page.reload();
    await expect(page.locator('.w-discord .dc-member').first()).toBeVisible();
    const px = (sel, prop) => page.locator(sel).first().evaluate((e, prop) => parseFloat(getComputedStyle(e)[prop]), prop);
    return { btn: await px('.w-discord .dc-btn', 'fontSize'), name: await px('.w-discord .dc-member', 'fontSize'), avatar: await px('.w-discord .dc-member .dc-ico', 'width') };
  };
  await page.setViewportSize({ width: 780, height: 360 });
  expect(await size(4, 3)).toEqual({ btn: 18, name: 12, avatar: 18 });
  await page.setViewportSize({ width: 1280, height: 800 });
  const big = await size(12, 6);
  expect(big.btn).toBeGreaterThanOrEqual(28);
  expect(big.name).toBeGreaterThanOrEqual(18);
  expect(big.avatar).toBeGreaterThanOrEqual(30);
  // the buttons never push the top row wider than the widget
  const top = await page.locator('.w-discord .dc-top').evaluate((e) => e.scrollWidth - e.clientWidth);
  expect(top).toBeLessThanOrEqual(0);
});
