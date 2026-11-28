#!/usr/bin/python3
# Native Postfix pipe transport. No listener, queue or retry loop lives here.
import base64
import json
import sys
import urllib.request

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

def main():
    queue_id, sender = sys.argv[1:3]
    recipients = sys.argv[3:]
    raw = sys.stdin.buffer.read(26214401)
    if len(raw) > 26214400:
        print('5.3.4 Message exceeds the LMS relay size limit')
        return 65
    try:
        with open('/etc/lms-mail-api.json', encoding='utf-8') as stream:
            config = json.load(stream)
        body = json.dumps(dict(queueId=queue_id, envelopeSender=sender,
                               recipients=recipients, mime=base64.b64encode(raw).decode('ascii'))).encode('utf-8')
        request = urllib.request.Request('http://127.0.0.1:5080/internal/mail-relay/deliver', data=body,
                    headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + config['token']})
        # Do not use environment proxies or follow redirects with the shared credential.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(request, timeout=60) as response:
            result = json.load(response)
        outcome = result['outcome']
        print(('2.0.0 ' if outcome == 0 else '5.0.0 ' if outcome == 2 else '4.3.0 ') + result['detail'])
        return 0 if outcome == 0 else 69 if outcome == 2 else 75
    except Exception:
        # An HTTP timeout never removes the queued mail. The LMS receipt claim
        # prevents a second provider submission if the first outcome was ambiguous.
        print('4.3.0 LMS delivery status could not be read; message remains in the Postfix queue')
        return 75

if __name__ == '__main__':
    sys.exit(main())
