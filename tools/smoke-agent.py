"""Launch one built/downloaded agent with isolated settings and validate its real API."""
import argparse,json,pathlib,re,ssl,subprocess,tempfile,time,urllib.request,urllib.error,os
p=argparse.ArgumentParser();p.add_argument('executable');p.add_argument('--version',required=True);p.add_argument('--port',type=int,default=18765);args=p.parse_args()
exe=pathlib.Path(args.executable).resolve()
if os.name!='nt':exe.chmod(exe.stat().st_mode|0o111)
base=f'http://127.0.0.1:{args.port}'
with tempfile.TemporaryDirectory(prefix='phonebox-smoke-') as work:
 config=pathlib.Path(work)/'config.json'
 # one shortcut with no program icon of its own (builtin:), for the uploaded-picture checks below
 config.write_text(json.dumps({'deviceSerial':'__smoke_no_device__','updates':{'auto':False},'lan':{'enabled':False},'claude':{'phoneApprovals':False},'pages':[{'template':'home'}],'rules':[],'shortcuts':[{'id':'smoke-art','label':'Art','cmd':'builtin:lock'}],'steam':{'enabled':False,'apiKey':'smoke-steam-key'},'mqtt':{'enabled':False,'host':'broker.invalid','username':'smoke-mqtt-user','password':'smoke-mqtt-password'}}))
 # A Windows helper may briefly inherit the agent's log handle. Keep capture outside the config directory
 # and let the temporary file's delete-on-close semantics handle it without masking configuration cleanup errors.
 with tempfile.TemporaryFile(mode='w+b') as log:
  proc=subprocess.Popen([str(exe),'--config',str(config),'--port',str(args.port)],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
  def get(path):return json.load(urllib.request.urlopen(base+path,timeout=3))
  try:
   deadline=time.monotonic()+40
   while True:
    if proc.poll() is not None:raise AssertionError('Agent exited before readiness')
    try:
     state=get('/api/state');procs=get('/api/procs?top=3')
     pc=state.get('pc') or {}
     if pc.get('cpu') is not None and pc.get('ramTotalGb',0)>0 and procs.get('groups'):break
    except (OSError,ValueError):pass
    assert time.monotonic()<deadline,'API readiness deadline exceeded'
    time.sleep(.3)
   assert get('/api/info')['version']==args.version,'Artifact version differs'
   catalog=get('/api/catalog');assert {'stack','codexbar'}<={w['type'] for w in catalog['widgets']}
   assert b'W.stack' in urllib.request.urlopen(base+'/panel/widgets/stack.js',timeout=3).read(),'Stack module missing from artifact'
   try:urllib.request.urlopen(urllib.request.Request(base+'/api/settings',headers={'Origin':'https://foreign.example'}),timeout=3)
   except urllib.error.HTTPError as error:assert error.code==403
   else:raise AssertionError('Foreign browser origin was accepted')
   # The phone's USB tunnel ends on a port of its own (named in the ready line): TLS, and nothing without the app's key
   deadline=time.monotonic()+10
   while True:
    log.seek(0);found=re.search(rb'phone USB port (\d+)',log.read())
    if found:break
    assert time.monotonic()<deadline,'USB port missing from the ready line'
    time.sleep(.3)
   usb=f'https://127.0.0.1:{int(found.group(1))}'
   tls=ssl.create_default_context();tls.check_hostname=False;tls.verify_mode=ssl.CERT_NONE
   def usb_status(path,method='GET',headers=None):
    req=urllib.request.Request(usb+path,method=method,headers=headers or {},data=b'{}' if method=='POST' else None)
    try:
     with urllib.request.urlopen(req,timeout=3,context=tls) as r:return r.status
    except urllib.error.HTTPError as error:return error.code
   assert usb_status('/api/state')==401,'USB port answered without a key'
   assert usb_status('/api/state',headers={'Authorization':'Bearer '+'A'*43})==401,'USB port accepted an unknown key'
   assert usb_status('/api/shortcuts/test','POST',{'Content-Type':'application/json'})==401,'USB port took a shortcut test without a key'
   assert usb_status('/api/settings',headers={'Origin':'https://foreign.example'})==403,'USB port accepted a browser origin'
   # Secrets in the settings: the MQTT password is write-only for every caller (the page gets passwordSet), and a plain
   # local request does not get Steam's key either. MQTT is off here, so the agent never connects anywhere.
   settings=urllib.request.urlopen(base+'/api/settings',timeout=3).read()
   assert b'smoke-mqtt-password' not in settings and json.loads(settings)['mqtt']['passwordSet'] is True,'MQTT password in the settings'
   assert b'smoke-steam-key' not in settings,'Steam key in a plain local settings request'
   phone_config=json.dumps(get('/api/config')).encode()
   assert not any(x in phone_config for x in (b'smoke-mqtt-password',b'smoke-mqtt-user',b'broker.invalid',b'mqtt')),'MQTT in the phone config'
   assert get('/api/mqtt/status')=={'state':'off','lastMessageAt':None,'errorKind':None},'MQTT status while off'
   # A filled Big button asks for the upload only, as upload=true: the real query binding must accept it, serve the
   # upload, and answer 404 (not the program icon, not 400) once the upload is removed.
   def status(path,method='GET',data=None,ctype=None):
    req=urllib.request.Request(base+path,data=data,method=method,headers={'Content-Type':ctype} if ctype else {})
    try:
     with urllib.request.urlopen(req,timeout=3) as r:return r.status,r.headers.get('Content-Type','')
    except urllib.error.HTTPError as error:return error.code,''
   art='/api/shortcuts/icon?id=smoke-art&upload=true'
   assert status(art)[0]==404,'Upload-only request answered without an upload'
   assert status('/api/settings/shortcut-icon?id=smoke-art','POST',b'<svg xmlns="http://www.w3.org/2000/svg"/>','image/svg+xml')[0]==200,'Icon upload failed'
   assert next(s for s in get('/api/config')['config']['shortcuts'] if s['id']=='smoke-art')['iconUpload'] is True,'Upload not reported to the phone'
   assert status(art)==(200,'image/svg+xml'),'Upload-only request did not serve the upload'
   assert status('/api/settings/shortcut-icon?id=smoke-art','DELETE')[0]==200,'Icon removal failed'
   assert status(art)[0]==404,'Upload-only request answered after the upload was removed'
   print(json.dumps({'version':args.version,'os':get('/api/info')['os'],'cpu':state['pc']['cpu'],'ramTotalGb':state['pc']['ramTotalGb'],'processGroups':len(procs['groups']),'widgets':len(catalog['widgets']),'foreignOrigin':403,'usbWithoutKey':401}))
  except BaseException:
   log.seek(0);print(log.read().decode(errors='replace'))
   raise
  finally:
   proc.terminate()
   try:proc.wait(timeout=10)
   except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=5)
