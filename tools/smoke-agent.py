"""Launch one built/downloaded agent with isolated settings and validate its real API."""
import argparse,json,pathlib,subprocess,tempfile,time,urllib.request,urllib.error,os
p=argparse.ArgumentParser();p.add_argument('executable');p.add_argument('--version',required=True);p.add_argument('--port',type=int,default=18765);args=p.parse_args()
exe=pathlib.Path(args.executable).resolve()
if os.name!='nt':exe.chmod(exe.stat().st_mode|0o111)
base=f'http://127.0.0.1:{args.port}'
with tempfile.TemporaryDirectory(prefix='phonebox-smoke-') as work:
 config=pathlib.Path(work)/'config.json'
 # one shortcut with no program icon of its own (builtin:), for the uploaded-picture checks below
 config.write_text(json.dumps({'deviceSerial':'__smoke_no_device__','updates':{'auto':False},'lan':{'enabled':False},'claude':{'phoneApprovals':False},'pages':[{'template':'home'}],'rules':[],'shortcuts':[{'id':'smoke-art','label':'Art','cmd':'builtin:lock'}]}))
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
   print(json.dumps({'version':args.version,'os':get('/api/info')['os'],'cpu':state['pc']['cpu'],'ramTotalGb':state['pc']['ramTotalGb'],'processGroups':len(procs['groups']),'widgets':len(catalog['widgets']),'foreignOrigin':403}))
  except BaseException:
   log.seek(0);print(log.read().decode(errors='replace'))
   raise
  finally:
   proc.terminate()
   try:proc.wait(timeout=10)
   except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=5)
