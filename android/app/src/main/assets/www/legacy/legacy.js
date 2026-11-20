/* ES5 only: Android 4.1 stock WebKit cannot parse the modern module graph. */
(function () {
    'use strict';
    var pages = [], current = 0, state = {}, widgets = [], history = {}, configVersion = '', busy = false;
    var names = {Home:'خانه',System:'سیستم',Music:'موسیقی',Focus:'تمرکز',World:'جهان','AI quotas':'سهمیهٔ هوش مصنوعی',Sessions:'نشست‌ها',Processes:'پردازش‌ها',Shortcuts:'میان‌برها'};
    var days = ['یکشنبه','دوشنبه','سه‌شنبه','چهارشنبه','پنجشنبه','جمعه','شنبه'];
    function id(s) { return document.getElementById(s); }
    function fa(v) { return String(v).replace(/[0-9]/g,function (c) { return '۰۱۲۳۴۵۶۷۸۹'.charAt(Number(c)); }); }
    function text(node,v) { node.textContent = v == null ? '—' : String(v); }
    function make(tag,cls,parent,value) { var e=document.createElement(tag); e.className=cls||''; if(value!=null){text(e,value);} if(parent){parent.appendChild(e);} return e; }
    function pad(v) { return v<10?'0'+v:String(v); }
    function request(path,body,done) {
        var x=new XMLHttpRequest(), finished=false;
        function finish(error,data){if(finished){return;}finished=true;clearTimeout(timer);done(error,data);}
        var timer=setTimeout(function(){finish(-1);x.abort();},8000);
        x.open(body==null?'GET':'POST',path,true);
        if(body!=null){x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');}
        x.onreadystatechange=function(){if(x.readyState!==4){return;}if(x.status!==200){finish(x.status||-1);return;}try{finish(null,JSON.parse(x.responseText));}catch(e){finish(-1);}};
        x.onerror=function(){finish(-1);};x.send(body==null?null:body);
    }
    function metric(key) {
        var pc=state.pc||{},g=pc.gpu||{},p=state.ping||{}, value, max=100,unit='٪',label=key;
        if(key==='cpu'){value=pc.cpu;label='پردازنده';}
        else if(key==='gpu'){value=g.util;label='گرافیک';}
        else if(key==='ram'){value=pc.ramUsedGb;max=pc.ramTotalGb;unit='GB';label='حافظه';}
        else if(key==='net'||key==='down'){value=pc.netDownBps;max=1048576;unit='KB/s';label='دریافت شبکه';value=value==null?null:value/1024;max=max/1024;}
        else if(key==='up'){value=pc.netUpBps;max=1024;unit='KB/s';label='ارسال شبکه';value=value==null?null:value/1024;}
        else if(key==='cpuTemp'){value=pc.cpuTemp;unit='°C';label='دمای پردازنده';}
        else if(key==='ping'){value=p.ms;max=200;unit='ms';label='پینگ';}
        else {value=null;label='دادهٔ پشتیبانی‌نشده';}
        return {value:value,max:max||100,unit:unit,label:label};
    }
    function metricString(m){return m.value==null?'—':fa(Math.round(m.value*10)/10)+' '+m.unit;}
    function remember(){var keys=['cpu','gpu','ram','down','up'],i,k;for(i=0;i<keys.length;i++){k=keys[i];if(!history[k]){history[k]=[];}history[k].push(metric(k).value);if(history[k].length>90){history[k].shift();}}}
    function spark(canvas,key){var list=history[key]||[],ctx=canvas.getContext&&canvas.getContext('2d'),w=canvas.clientWidth||240,h=canvas.clientHeight||65,i,max=1,v;if(!ctx){return;}canvas.width=w;canvas.height=h;for(i=0;i<list.length;i++){max=Math.max(max,list[i]||0);}ctx.strokeStyle='#91addb';ctx.lineWidth=2;ctx.beginPath();for(i=0;i<list.length;i++){v=list[i];if(v==null){continue;}if(i===0){ctx.moveTo(0,h-(v/max)*(h-5));}else{ctx.lineTo(i*w/Math.max(list.length-1,1),h-(v/max)*(h-5));}}ctx.stroke();}
    function buildWidget(w,box) {
        var o=w.options||{},type=w.type,update=function(){},label,value,sub,fill,canvas,key,rows,i,timer;
        if(type==='clock'||type==='wordclock'||type==='worldclock'){
            value=make('div','clock',box);sub=make('div','clock-date',box);
            update=function(){var d=new Date();text(value,fa(pad(d.getHours())+':'+pad(d.getMinutes())+':'+pad(d.getSeconds())));text(sub,days[d.getDay()]+' · '+fa(d.getFullYear()+'/'+(d.getMonth()+1)+'/'+d.getDate()));};
        }else if(type==='stat'||type==='gauge'||type==='chart'){
            key=o.metric||(o.metrics&&o.metrics[0])||'cpu';label=make('div','caption',box);value=make('div','big',box);fill=make('div','fill',make('div','track',box));
            if(type==='chart'){canvas=make('canvas','',box);}
            update=function(){var m=metric(key);text(label,m.label);text(value,metricString(m));fill.style.width=Math.max(0,Math.min(100,(m.value||0)/m.max*100))+'%';if(canvas){spark(canvas,key);}};
        }else if(type==='meters'||type==='gpu'){
            make('div','caption',box,type==='gpu'?'گرافیک':'حافظه و دیسک');value=make('div','sub',box);
            update=function(){var pc=state.pc||{},g=pc.gpu||{},d=pc.disks||[],lines=[],j;if(type==='gpu'){lines.push(g.name||'—');lines.push('استفاده: '+fa(g.util==null?'—':g.util)+'٪');lines.push('دما: '+fa(g.temp==null?'—':g.temp)+' °C');}else{lines.push('RAM: '+fa(pc.ramUsedGb||0)+' / '+fa(pc.ramTotalGb||0)+' GB');for(j=0;j<d.length;j++){lines.push(d[j].name+': '+fa(d[j].usedGb)+' / '+fa(d[j].totalGb)+' GB');}}text(value,lines.join('\n'));};
        }else if(type==='media'){
            make('div','caption',box,'در حال پخش روی مک');value=make('div','big',box);sub=make('div','sub',box);
            update=function(){var m=state.media;text(value,m&&m.title?m.title:'موسیقی پخش نمی‌شود');text(sub,m?(m.artist||m.app||''): 'یک آهنگ روی مک پخش کن');};
        }else if(type==='note'||type==='title'){
            make('div',type==='title'?'big':'note',box,o.text||o.title||'');
        }else if(type==='calendar'){
            value=make('div','note center',box);update=function(){var d=new Date();text(value,days[d.getDay()]+'\n'+fa(d.getDate())+' / '+fa(d.getMonth()+1)+' / '+fa(d.getFullYear()));};
        }else if(type==='pomodoro'){
            make('div','caption',box,'تمرکز');value=make('div','clock',box);timer={remaining:1500,deadline:0,running:false};var controls=make('div','timer-controls center',box),toggle=make('button','',controls,'شروع'),reset=make('button','',controls,'از اول');
            toggle.onclick=function(){if(timer.running){timer.remaining=Math.max(0,Math.ceil((timer.deadline-new Date().getTime())/1000));timer.running=false;}else{if(!timer.remaining){timer.remaining=1500;}timer.deadline=new Date().getTime()+timer.remaining*1000;timer.running=true;}update();};
            reset.onclick=function(){timer.remaining=1500;timer.running=false;update();};
            update=function(){if(timer.running){timer.remaining=Math.max(0,Math.ceil((timer.deadline-new Date().getTime())/1000));if(!timer.remaining){timer.running=false;}}text(value,fa(pad(Math.floor(timer.remaining/60))+':'+pad(timer.remaining%60)));text(toggle,timer.running?'توقف':'شروع');};
        }else{
            make('div','caption',box,type||'Widget');make('div','unsupported',box,'این ویجت هنوز در حالت اندروید قدیمی پشتیبانی نمی‌شود.');
        }
        update();return update;
    }
    function layout(){var board=id('board'),h=Math.max(280,(window.innerHeight||600)-175),p=pages[current],list=board.children,i,w;board.style.height=h+'px';if(!p){return;}for(i=0;i<list.length;i++){w=p.widgets[i];list[i].style.left=(w.x||0)/12*100+'%';list[i].style.top=(w.y||0)/6*100+'%';list[i].style.width=(w.w||3)/12*100+'%';list[i].style.height=(w.h||2)/6*100+'%';}}
    function show(index){if(!pages.length){text(id('status'),'صفحهٔ فعالی وجود ندارد؛ در تنظیمات مک یک صفحه اضافه کن.');return;}current=(index+pages.length)%pages.length;widgets=[];var board=id('board'),p=pages[current],i,tile;while(board.firstChild){board.removeChild(board.firstChild);}for(i=0;i<(p.widgets||[]).length;i++){tile=make('div','tile',board);widgets.push(buildWidget(p.widgets[i],make('div','surface',tile)));}for(i=0;i<id('tabs').children.length;i++){id('tabs').children[i].className=i===current?'active':'';}text(id('pageName'),(names[p.title]||p.title)+' · '+fa(current+1)+' / '+fa(pages.length));layout();try{localStorage.setItem('phonebox-legacy-page',p.id);}catch(e){}}
    function configure(data){var cfg=data.config||{},all=cfg.pages||[],old=pages[current]&&pages[current].id,i,button;pages=[];while(id('tabs').firstChild){id('tabs').removeChild(id('tabs').firstChild);}for(i=0;i<all.length;i++){if(all[i].enabled!==false){pages.push(all[i]);}}for(i=0;i<pages.length;i++){button=make('button','',id('tabs'),names[pages[i].title]||pages[i].title||'Page');button.onclick=(function(n){return function(){show(n);};})(i);}if(!old){try{old=localStorage.getItem('phonebox-legacy-page');}catch(e){}}current=0;for(i=0;i<pages.length;i++){if(pages[i].id===old){current=i;}}show(current);}
    function loadConfig(done){request('/api/config',null,function(error,data){if(!error&&data.version!==configVersion){configVersion=data.version;configure(data);}if(done){done(error);}});}
    function poll(){if(busy){return;}busy=true;request('/api/state',null,function(error,data){busy=false;if(error===401){paired(false);return;}if(error){text(id('status'),'ارتباط قطع است؛ مک و وای‌فای را بررسی کن');return;}state=data;remember();text(id('status'),'متصل به PhoneBox · زنده');refresh();});}
    function refresh(){for(var i=0;i<widgets.length;i++){widgets[i]();}}
    function paired(ok){id('pair').style.display=ok?'none':'block';id('panel').style.display=ok?'block':'none';if(ok){loadConfig(function(){poll();});}else{text(id('status'),'کد اتصال لازم است');}}
    id('pairForm').onsubmit=function(e){if(e&&e.preventDefault){e.preventDefault();}request('/pair','pin='+encodeURIComponent(id('pin').value),function(error){text(id('pairError'),error===429?'یک دقیقه صبر کن.':error?'کد درست نیست یا مک در دسترس نیست.':'');if(!error){paired(true);}});return false;};
    id('previous').onclick=function(){show(current-1);};id('next').onclick=function(){show(current+1);};
    id('fullscreen').onclick=function(){var e=document.documentElement,fn=e.requestFullscreen||e.webkitRequestFullScreen;if(fn){fn.call(e);}else{text(id('status'),'تبلت را افقی بگیر؛ زمان خواب صفحه را در تنظیمات تبلت افزایش بده.');}};
    window.onresize=layout;setInterval(refresh,1000);setInterval(function(){if(id('panel').style.display!=='none'){poll();}},5000);setInterval(function(){if(id('panel').style.display!=='none'){loadConfig();}},15000);
    request('/api/config',null,function(error,data){if(error){paired(false);return;}configVersion=data.version;configure(data);id('pair').style.display='none';id('panel').style.display='block';layout();poll();});
}());
