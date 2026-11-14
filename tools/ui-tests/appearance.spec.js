const {test,expect}=require('@playwright/test');

async function show(page, config={}) {
  await page.goto('/panel/');
  await page.evaluate(config=>{
    localStorage.clear();
    localStorage.setItem('pb-demo-config',JSON.stringify({night:{enabled:false},shortcuts:[],display:{idleReturnSec:0},
      pages:[{id:'appearance',title:'Appearance',widgets:[
        {id:'note',type:'note',x:0,y:0,w:6,h:6,options:{text:'Appearance sample'}},
        {id:'audio',type:'audio-mixer',x:6,y:0,w:6,h:6,options:{}}
      ]}],...config}));
  },config);
  await page.reload();
  await expect(page.locator('.w-note').first()).toBeVisible();
}
const css=(locator,key)=>locator.evaluate((node,key)=>getComputedStyle(node)[key],key);

test('original pages retain the original card and grid appearance',async({page})=>{
  await show(page);
  await expect.poll(()=>css(page.locator('.grid'),'gap')).toBe('8px');
  await expect.poll(()=>css(page.locator('.w-note'),'borderTopWidth')).toBe('1px');
  expect(await css(page.locator('.w-note'),'boxShadow')).toBe('none');
});

test('flat appearance has independent rounded corners, spacing and padding',async({page})=>{
  await show(page,{display:{appearance:{style:'flat',radius:30,gap:16,padding:7}}});
  await expect.poll(()=>css(page.locator('.grid'),'gap')).toBe('16px');
  await expect.poll(()=>css(page.locator('.w-note'),'borderTopLeftRadius')).toBe('30px');
  expect(await css(page.locator('.w-note'),'paddingLeft')).toBe('7px');
  expect(await css(page.locator('.w-note'),'borderTopWidth')).toBe('0px');
  expect(await css(page.locator('.w-note'),'boxShadow')).toBe('none');
  expect(await css(page.locator('.w-note'),'backgroundImage')).toBe('none');
});

test('page and widget overrides win and explicit false and zero survive',async({page})=>{
  await show(page,{display:{appearance:{style:'cards',radius:24,gap:18,border:true,shadow:true}},
    pages:[{id:'p',title:'P',appearance:{gap:0,radius:12},widgets:[
      {id:'a',type:'note',x:0,y:0,w:6,h:6,appearance:{radius:0,padding:0,border:false,shadow:false},options:{}},
      {id:'b',type:'note',x:6,y:0,w:6,h:6,options:{}}
    ]}]});
  const cards=page.locator('.w-note');
  await expect.poll(()=>css(page.locator('.grid'),'gap')).toBe('0px');
  expect(await css(cards.nth(0),'borderTopLeftRadius')).toBe('0px');
  expect(await css(cards.nth(0),'paddingLeft')).toBe('0px');
  expect(await css(cards.nth(0),'borderTopWidth')).toBe('0px');
  expect(await css(cards.nth(0),'boxShadow')).toBe('none');
  expect(await css(cards.nth(1),'borderTopLeftRadius')).toBe('12px');
  expect(await css(cards.nth(1),'boxShadow')).not.toBe('none');
});

test('an original-look page resets a global preset',async({page})=>{
  await show(page,{display:{appearance:{style:'seamless',radius:0,padding:0}},
    pages:[{id:'p',title:'P',appearance:{style:'default'},widgets:[{id:'n',type:'note',x:0,y:0,w:12,h:6,options:{}}]}]});
  await expect.poll(()=>css(page.locator('.grid'),'gap')).toBe('8px');
  expect(parseFloat(await css(page.locator('.w-note'),'borderTopLeftRadius'))).toBeGreaterThan(0);
  expect(parseFloat(await css(page.locator('.w-note'),'paddingLeft'))).toBeGreaterThan(0);
});

test('page presets inherit global edge padding that the preset does not override',async({page})=>{
  await show(page,{display:{appearance:{style:'cards',pagePadding:24}},pages:[
    {id:'p',title:'P',appearance:{style:'flat'},widgets:[{id:'n',type:'note',x:0,y:0,w:12,h:6,options:{}}]}
  ]});
  await expect.poll(()=>css(page.locator('.page'),'paddingLeft')).toBe('24px');
  expect(await css(page.locator('.page'),'paddingBottom')).toBe('24px');
});

test('desktop appearance preview applies unsaved changes and Save persists them',async({page})=>{
  await show(page);
  await page.setViewportSize({width:1280,height:1000});
  await page.goto('/settings/#general');
  const editor=page.locator('#appearanceSettings');
  await editor.getByRole('combobox',{name:'Surface style',exact:true}).selectOption('flat');
  const radius=editor.getByRole('slider',{name:'Corner radius',exact:true});
  await expect(radius).toHaveValue('12');
  await radius.focus();await radius.press('Home');
  const spacing=editor.getByRole('slider',{name:'Widget spacing',exact:true});
  await spacing.focus();await spacing.press('ArrowLeft');
  await editor.getByRole('button',{name:'Show live appearance preview',exact:true}).click();
  const frame=page.frameLocator('iframe[title="Live appearance preview"]');
  await expect.poll(()=>css(frame.locator('.grid'),'gap')).toBe('11px');
  await expect.poll(()=>css(frame.locator('.w-note'),'borderTopLeftRadius')).toBe('0px');
  const before=await page.evaluate(async()=>(await(await fetch('/api/settings')).json()).display?.appearance);
  expect(before).toBeUndefined();
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  await page.reload();
  await expect(editor.getByRole('combobox',{name:'Surface style',exact:true})).toHaveValue('flat');
  await expect(editor.getByRole('slider',{name:'Corner radius',exact:true})).toHaveValue('0');
  await editor.getByRole('button',{name:'Reset to inherited appearance',exact:true}).click();
  await page.locator('#save').click();await expect(page.locator('#save')).toBeDisabled();
  const after=await page.evaluate(async()=>(await(await fetch('/api/settings')).json()).display?.appearance);
  expect(after).toBeUndefined();
});

test('all catalog widgets and Smart Stack children accept the common flat style',async({page})=>{
  await page.goto('/panel/');
  const count=await page.evaluate(async()=>{
    localStorage.clear();const catalog=await(await fetch('../widgets.json')).json();
    localStorage.setItem('pb-demo-config',JSON.stringify({night:{enabled:false},display:{idleReturnSec:0,appearance:{style:'flat',radius:0,padding:8}},shortcuts:[],
      pages:catalog.map(w=>({id:w.type,title:w.name,widgets:[{...w,id:w.type,x:0,y:0,options:Object.fromEntries((w.options||[]).map(o=>[o.key,o.default]))}]}))}));
    return catalog.length;
  });
  await page.reload();await expect(page.locator('#dots i')).toHaveCount(count);
  const bad=await page.locator('.w').evaluateAll(nodes=>nodes.filter(node=>{
    const c=getComputedStyle(node);return c.borderTopLeftRadius!=='0px'||c.paddingLeft!=='8px'||c.boxShadow!=='none';
  }).map(node=>node.className));
  expect(bad).toEqual([]);
});

test('page and widget appearance editors preserve their own saved overrides',async({page})=>{
  await show(page,{display:{appearance:{style:'cards',radius:24}}});
  await page.setViewportSize({width:1280,height:1000});await page.goto('/settings/#pages');
  const pageEditor=page.locator('details[aria-label="Page appearance"]');
  await pageEditor.locator('summary').click();
  await pageEditor.getByRole('combobox',{name:'Surface style',exact:true}).selectOption('seamless');
  await page.locator('.wb').first().click();
  const widgetEditor=page.locator('details[aria-label="Widget appearance"]');
  await widgetEditor.locator('summary').click();
  await widgetEditor.getByRole('combobox',{name:'Surface style',exact:true}).selectOption('flat');
  const radius=widgetEditor.getByRole('slider',{name:'Corner radius',exact:true});
  await radius.focus();await radius.press('End');
  await page.locator('#save').click();await expect(page.locator('#save')).toBeDisabled();
  await page.goto('/panel/');
  await expect.poll(()=>css(page.locator('.grid'),'gap')).toBe('0px');
  expect(await css(page.locator('.w-note'),'borderTopLeftRadius')).toBe('32px');
  expect(await css(page.locator('.w-note'),'borderTopWidth')).toBe('0px');
  expect(await css(page.locator('.w-audio-mixer'),'borderTopLeftRadius')).toBe('0px');
});

for(const width of [780,360])test('appearance fits the panel at '+width+'px',async({page})=>{
  await page.setViewportSize({width,height:width===780?360:780});
  await show(page,{display:{appearance:{style:'flat',radius:24,gap:16,padding:8,pagePadding:4}}});
  const grid=page.locator('.grid');
  await expect.poll(()=>css(grid,'gap')).toBe('16px');
  expect(await grid.evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
  await page.screenshot({path:'../../.temp_files/appearance-flat-'+width+'.png'});
});

test('global font and font scale affect widgets proportionally without compounding em units',async({page})=>{
  const basePages=[{id:'p',title:'P',widgets:[
    {id:'clk',type:'clock',x:0,y:0,w:6,h:4,options:{}},
    {id:'note',type:'note',x:6,y:0,w:6,h:4,options:{text:'Tablet scale'}},
    {id:'audio',type:'audio-mixer',x:0,y:4,w:12,h:2,options:{}}
  ]}];
  await show(page,{display:{showSeconds:true,idleReturnSec:0},pages:basePages});
  await expect.poll(()=>page.locator('.w-clock .clock').count()).toBe(1);
  const baseClock=parseFloat(await css(page.locator('.w-clock .clock'),'fontSize'));
  const baseSec=parseFloat(await css(page.locator('.w-clock .sec'),'fontSize'));
  const baseDate=parseFloat(await css(page.locator('.w-clock .date'),'fontSize'));
  const baseNote=parseFloat(await css(page.locator('.w-note .note-t'),'fontSize'));
  const baseAudioTitle=parseFloat(await css(page.locator('.w-audio-mixer .w-title'),'fontSize'));

  await show(page,{display:{showSeconds:true,idleReturnSec:0,appearance:{font:'mono',fontScale:150}},pages:basePages});
  await expect.poll(async()=>parseFloat(await css(page.locator('.w-note .note-t'),'fontSize'))).toBeCloseTo(baseNote*1.5,0);
  const scaledClock=parseFloat(await css(page.locator('.w-clock .clock'),'fontSize'));
  const scaledSec=parseFloat(await css(page.locator('.w-clock .sec'),'fontSize'));
  const scaledDate=parseFloat(await css(page.locator('.w-clock .date'),'fontSize'));
  const scaledAudioTitle=parseFloat(await css(page.locator('.w-audio-mixer .w-title'),'fontSize'));
  expect(scaledClock).toBeCloseTo(baseClock*1.5,0);
  expect(scaledSec).toBeCloseTo(baseSec*1.5,0);
  expect(scaledDate).toBeCloseTo(baseDate*1.5,0);
  expect(scaledAudioTitle).toBeCloseTo(baseAudioTitle*1.5,0);
  expect(await css(page.locator('.w-note'),'fontFamily')).toMatch(/Mono|monospace/i);
  expect(await css(page.locator('#offline'),'fontSize')).toBe('11.5px');
});

test('page, widget and Smart Stack font and scale overrides compose correctly',async({page})=>{
  await show(page,{display:{idleReturnSec:0,appearance:{font:'mono',fontScale:140}},pages:[{id:'p',title:'P',
    appearance:{font:'serif',fontScale:125},widgets:[
      {id:'a',type:'note',x:0,y:0,w:4,h:6,appearance:{font:'default',fontScale:100},options:{text:'Default'}},
      {id:'b',type:'note',x:4,y:0,w:4,h:6,options:{text:'Page serif'}},
      {id:'c',type:'stack',x:8,y:0,w:4,h:6,appearance:{font:'rounded',fontScale:150},options:{widgets:[{id:'sn',type:'note',options:{text:'Stack child'}}]}}
    ]}]});
  await expect.poll(()=>page.locator('.w-note').count()).toBe(3);
  const notes=page.locator('.w-note');
  const baseNote=parseFloat(await css(notes.nth(0).locator('.note-t'),'fontSize'));
  expect(await css(notes.nth(0),'fontFamily')).toMatch(/Roboto|sans-serif/i);
  expect(await css(notes.nth(1),'fontFamily')).toMatch(/Georgia|serif/i);
  expect(parseFloat(await css(notes.nth(1).locator('.note-t'),'fontSize'))).toBeCloseTo(baseNote*1.25,1);
  expect(await css(notes.nth(2),'fontFamily')).toMatch(/Rounded|Nunito|sans-serif/i);
  expect(parseFloat(await css(notes.nth(2).locator('.note-t'),'fontSize'))).toBeCloseTo(baseNote*1.5,1);
});

test('desktop settings font and font size controls preview and persist',async({page})=>{
  await show(page);
  const baseNote=parseFloat(await css(page.locator('.w-note .note-t'),'fontSize'));
  await page.setViewportSize({width:1280,height:1000});
  await page.goto('/settings/#general');
  const editor=page.locator('#appearanceSettings');
  await editor.getByRole('combobox',{name:'Font',exact:true}).selectOption('mono');
  const fontSize=editor.getByRole('slider',{name:'Font size',exact:true});
  await expect(fontSize).toHaveValue('100');
  await fontSize.focus();await fontSize.press('End');
  await editor.getByRole('button',{name:'Show live appearance preview',exact:true}).click();
  const frame=page.frameLocator('iframe[title="Live appearance preview"]');
  await expect.poll(()=>css(frame.locator('.w-note'),'fontFamily')).toMatch(/Mono|monospace/i);
  await expect.poll(async()=>parseFloat(await css(frame.locator('.w-audio-mixer .w-title'),'fontSize'))).toBeCloseTo(22,0);
  await page.locator('#save').click();await expect(page.locator('#save')).toBeDisabled();
  await page.goto('/settings/#pages');
  await page.locator('.wb').first().click();
  const widgetEditor=page.locator('details[aria-label="Widget appearance"]');
  await widgetEditor.locator('summary').click();
  await widgetEditor.getByRole('combobox',{name:'Font',exact:true}).selectOption('serif');
  await widgetEditor.getByRole('slider',{name:'Font size',exact:true}).fill('80');
  await page.locator('#save').click();await expect(page.locator('#save')).toBeDisabled();
  await page.goto('/panel/');
  await expect.poll(()=>css(page.locator('.w-note'),'fontFamily')).toMatch(/Georgia|serif/i);
  expect(parseFloat(await css(page.locator('.w-note .note-t'),'fontSize'))).toBeCloseTo(baseNote*0.8,0);
  expect(await css(page.locator('.w-audio-mixer'),'fontFamily')).toMatch(/Mono|monospace/i);
  expect(parseFloat(await css(page.locator('.w-audio-mixer .w-title'),'fontSize'))).toBeCloseTo(22,0);
});

test('compact 4-limit quota percentage text scales with widget fontScale',async({page})=>{
  const sampleQuota = {
    providers: [{
      id: 'claude', name: 'Claude', plan: 'Max', updatedAt: Date.now(),
      limits: [
        { label: '5h', percent: 40, resetsAt: Date.now() + 3600_000 },
        { label: '7d', percent: 55, resetsAt: Date.now() + 86400_000 },
        { label: 'Opus', percent: 25, resetsAt: Date.now() + 86400_000 },
        { label: 'Sonnet', percent: 70, resetsAt: Date.now() + 86400_000 },
      ],
    }],
  };
  await page.goto('/panel/');
  await page.evaluate(()=>{
    localStorage.clear();
    localStorage.setItem('pb-demo-config',JSON.stringify({
      night:{enabled:false},shortcuts:[],display:{idleReturnSec:0},
      pages:[{id:'q',title:'Quotas',widgets:[
        {id:'q1',type:'quotas',x:0,y:0,w:6,h:6,appearance:{fontScale:100},options:{providers:['claude']}},
        {id:'q2',type:'quotas',x:6,y:0,w:6,h:6,appearance:{fontScale:150},options:{providers:['claude']}}
      ]}]
    }));
  });
  await page.reload();
  await expect(page.locator('.w-quotas')).toHaveCount(2);
  await page.evaluate(async(q)=>{
    const { D, emit } = await import('/panel/main.js');
    D.quotas = q;
    emit('quotas');
  }, sampleQuota);
  await expect(page.locator('.q-rings-grid .q-pct')).toHaveCount(8);
  const basePct = parseFloat(await css(page.locator('.w-quotas').nth(0).locator('.q-rings-grid .q-pct').first(),'fontSize'));
  const scaledPct = parseFloat(await css(page.locator('.w-quotas').nth(1).locator('.q-rings-grid .q-pct').first(),'fontSize'));
  expect(basePct).toBeCloseTo(22, 0);
  expect(scaledPct).toBeCloseTo(33, 0);
});

