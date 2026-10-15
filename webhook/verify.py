import os,base64,struct,subprocess,json,time,hashlib,urllib.request,urllib.parse,urllib.error,sqlite3
for line in open('/etc/beisen-webhook.env'):
 k,v=line.strip().split('=',1); os.environ[k]=v
key=base64.b64decode(os.environ['BEISEN_AES_KEY']+'=')
host=os.environ.get('BEISEN_BIND','0.0.0.0'); host='127.0.0.1' if host=='0.0.0.0' else host
def encrypt(msg):
 raw=b'0123456789abcdef'+struct.pack('!I',len(msg))+msg
 n=32-len(raw)%32; raw+=bytes([n])*n
 p=subprocess.run(['openssl','enc','-aes-256-cbc','-K',key.hex(),'-iv',key[:16].hex(),'-nopad'],input=raw,capture_output=True,check=True)
 return base64.b64encode(p.stdout).decode()
def send(msg,echo=False,bad=False):
 enc=encrypt(msg); ts=str(int(time.time()*1000)); nonce='local-verification'
 sig=hashlib.sha1(''.join(sorted([os.environ['BEISEN_TOKEN'],ts,nonce,enc])).encode()).hexdigest()
 q=urllib.parse.urlencode(dict(timestamp=ts,nonce=nonce,msg_signature='0'*40 if bad else sig,echostr=enc if echo else ''))
 req=urllib.request.Request(f'http://{host}:8600/beisen/events?'+q,data=b'' if echo else json.dumps({'msg_encrypt':enc}).encode())
 start=time.monotonic()
 try:
  with urllib.request.urlopen(req,timeout=2) as r: return r.status,r.read(),time.monotonic()-start
 except urllib.error.HTTPError as e: return e.code,e.read(),time.monotonic()-start
assert send(b'verify-no-newline',True)[:2]==(200,b'verify-no-newline')
assert send(b'invalid',True,True)[0]==403
# 拉取接口（8601）启用后不再写库：合成事件会被 AD 侧监听当成真实事件；删掉又会让序号被下一条真实事件复用，监听会跳过它
if os.environ.get('BEISEN_FEED_TOKEN'):
 print('PASS: challenge, signature rejection. Persistence test skipped (feed API enabled; synthetic rows would reach the AD-side listener).')
 raise SystemExit(0)
ids=[]
for category in ['OrgEvent','EmpEvent']:
 event_id='local-test-'+category+'-'+str(time.time_ns()); ids.append(event_id)
 payload=json.dumps(dict(tenant_id=0,biz_id=event_id,biz_typeCode=category,biz_data='{}')).encode()
 for i in range(2):
  result=send(payload); assert result[0]==200,result
  print(category,'attempt',i+1,'latency',round(result[2],3),'s')
c=sqlite3.connect('/var/lib/beisen-webhook/events.sqlite3')
for eid in ids:
 assert c.execute('select count(*) from events where tenant_id=? and biz_id=?',('0',eid)).fetchone()[0]==1
 c.execute('delete from events where tenant_id=? and biz_id=?',('0',eid))
c.commit()
print('PASS: challenge, signature rejection, org/employee persistence, duplicate handling. Synthetic rows removed.')
