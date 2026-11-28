import base64, http.server, json, os, subprocess, threading, time
calls = []
accept_deferred = False
class Endpoint(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        assert self.path == '/internal/mail-relay/deliver'
        assert self.headers.get('Authorization') == 'Bearer fixture-only'
        body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        assert body['queueId'] and 1 <= len(body['recipients']) <= 50
        mime=base64.b64decode(body['mime'])
        assert b'Message-ID: <fixture@example.test>\r\n' in mime
        assert b'\r\n\r\nNative queue test' in mime
        assert body['envelopeSender'] == 'lms@example.test'
        calls.append(body)
        outcome=1 if body['recipients'][0] == 'defer@example.test' and not accept_deferred else 0
        data=json.dumps(dict(outcome=outcome,detail='fixture accepted' if outcome==0 else 'fixture deferred')).encode()
        self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
    def log_message(self,*args): pass
open('/etc/lms-mail-api.json','w').write(json.dumps(dict(token='fixture-only')))
subprocess.run(['chown','lmsmailapi:lmsmailapi','/etc/lms-mail-api.json'],check=True);os.chmod('/etc/lms-mail-api.json',0o600)
server=http.server.ThreadingHTTPServer(('127.0.0.1',5080),Endpoint);threading.Thread(target=server.serve_forever,daemon=True).start()
subprocess.run(['postconf','-e','myhostname=smtp.example.test','mydestination=','inet_interfaces=loopback-only',
 'default_transport=lmsapi:localhost','relay_transport=lmsapi:localhost','lmsapi_destination_recipient_limit=50',
 'lmsapi_time_limit=75s','smtpd_milters=','non_smtpd_milters='],check=True)
with open('/etc/postfix/master.cf','a') as stream:
    stream.write('\nlmsapi unix - n n - 4 pipe\n  flags= user=lmsmailapi eol=\\r\\n argv=/usr/bin/python3 /usr/local/bin/lms-mail-api ${queue_id} ${sender} ${recipient}\n')
subprocess.run(['postfix','start'],check=True)
message=b'Message-ID: <fixture@example.test>\nFrom: lms@example.test\nTo: one@example.test\nCc: two@example.test\n\nNative queue test\n'
subprocess.run(['sendmail','-f','lms@example.test','--','one@example.test','two@example.test'],input=message,check=True)
for _ in range(50):
    if len(calls)>=1: break
    time.sleep(.2)
assert len(calls)==1, ('expected one native batch',calls)
assert set(calls[0]['recipients']) == {'one@example.test','two@example.test'}
time.sleep(.5)
assert not subprocess.check_output(['postqueue','-j']).strip(), 'accepted messages must leave original Postfix queue'
subprocess.run(['sendmail','-f','lms@example.test','--','defer@example.test'],input=message,check=True)
for _ in range(50):
    if len(calls)>=2: break
    time.sleep(.2)
assert len(calls)==2
queued=json.loads(subprocess.check_output(['postqueue','-j']).strip())
assert queued['recipients'][0]['address']=='defer@example.test', queued
accept_deferred = True
subprocess.run(['postqueue','-f'], check=True)
for _ in range(50):
    if len(calls)>=3: break
    time.sleep(.2)
assert len(calls)==3, calls
assert calls[1] == calls[2], 'native retries must retain the same envelope, queue ID and MIME fingerprint'
time.sleep(.5)
assert not subprocess.check_output(['postqueue','-j']).strip()
print('PASS: stable retry identity, unprivileged pipe, queue IDs, CRLF MIME, envelope recipients, accepted dequeue and temporary native queue deferral')
subprocess.run(['postfix','stop'],check=True)
