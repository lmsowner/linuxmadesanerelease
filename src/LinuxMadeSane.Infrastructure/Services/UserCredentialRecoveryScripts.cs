// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Infrastructure.Services;

internal static class UserCredentialRecoveryScripts
{
    internal const string Rollback = """
import os,sys,json,fcntl,base64,stat,shutil,tempfile,subprocess,time
root=sys.argv[1]
force=len(sys.argv)>2 and sys.argv[2]=='force'
with open(root+'/lock','a') as lock:
 fcntl.flock(lock,fcntl.LOCK_EX)
 if os.path.exists(root+'/kept') and not force: sys.exit(0)
 if os.path.exists(root+'/restored'): sys.exit(0)
 with open(root+'/snapshot.json') as f: saved=json.load(f)
 for entry in saved['files']:
  path=entry['path']
  if entry['content'] is None:
   if os.path.lexists(path): os.unlink(path)
  else:
   os.makedirs(os.path.dirname(path),exist_ok=True)
   fd,tmp=tempfile.mkstemp(dir=os.path.dirname(path))
   try:
    with os.fdopen(fd,'wb') as f:f.write(base64.b64decode(entry['content']))
    os.chmod(tmp,entry['mode']);os.chown(tmp,entry['uid'],entry['gid']);os.replace(tmp,path)
   finally:
    if os.path.exists(tmp):os.unlink(tmp)
 if saved['shadow'] is not None:
  with os.fdopen(os.open('/etc/.pwd.lock',os.O_WRONLY|os.O_CREAT,0o600),'a') as pwlock:
   fcntl.lockf(pwlock,fcntl.LOCK_EX)
   with open('/etc/shadow') as f:lines=f.readlines()
   found=False
   for index,line in enumerate(lines):
    parts=line.rstrip('\n').split(':')
    if parts[0]==saved['user']:
     old=saved['shadow'].split(':');parts[1:3]=old[1:3];lines[index]=':'.join(parts)+'\n';found=True;break
   if not found:raise RuntimeError('Cannot restore credentials: Linux account is missing')
   fd,tmp=tempfile.mkstemp(dir='/etc')
   try:
    shutil.copy2('/etc/shadow',tmp)
    with os.fdopen(fd,'w') as f:f.writelines(lines);f.truncate()
    source=os.stat('/etc/shadow');os.chown(tmp,source.st_uid,source.st_gid);os.replace(tmp,'/etc/shadow')
   finally:
    if os.path.exists(tmp):os.unlink(tmp)
 subprocess.run(['/usr/sbin/sshd','-t'],check=True,stdout=subprocess.DEVNULL)
 for unit in ['ssh.service','sshd.service']:
  if subprocess.run(['systemctl','is-active','--quiet',unit]).returncode==0:
   subprocess.run(['systemctl','reload',unit],check=True);break
 else:raise RuntimeError('Cannot reload SSH: service is not active')
 with open(root+'/restored','w') as f:f.write('restored')
""";

    internal const string Control = """
import os,sys,json,re,time,base64,stat,shutil,subprocess,fcntl
operation,identifier,user=sys.argv[1:4]
if os.geteuid()!=0:raise RuntimeError('Root privileges are required to protect credential recovery')
if not re.fullmatch('[a-f0-9]{32}',identifier) or not re.fullmatch('[a-zA-Z_][a-zA-Z0-9_.-]*[$]?',user):raise RuntimeError('Invalid trial identifier or Linux username')
parent='/etc/ssh/linuxmadesane/login-trials'
os.makedirs(parent,mode=0o700,exist_ok=True)
if os.path.islink(parent) or os.stat(parent).st_uid!=0:raise RuntimeError('Unsafe recovery directory')
os.chmod(parent,0o700)
root=parent+'/'+identifier
unit='lms-user-login-trial-'+identifier
if operation=='snapshot':
 payload=json.load(sys.stdin)
 subprocess.run(['/usr/sbin/sshd','-t'],check=True,stdout=subprocess.DEVNULL)
 payload['seconds']=max(1,__import__('math').ceil(payload['expires']-time.time())) if 'expires' in payload else payload['seconds']
 os.mkdir(root,0o700)
 files=[]
 for path in ['/etc/ssh/sshd_config.d/00-linuxmadesane-user-'+user+'.conf','/etc/ssh/linuxmadesane/local-users/authorized_keys/'+user]:
  if os.path.islink(path):raise RuntimeError('Cannot trial credentials stored in a symlink')
  entry={'path':path,'content':None}
  if os.path.exists(path):
   st=os.stat(path)
   with open(path,'rb') as f:entry.update(content=base64.b64encode(f.read()).decode(),mode=stat.S_IMODE(st.st_mode),uid=st.st_uid,gid=st.st_gid)
  files.append(entry)
 shadow=None
 if payload['changesPassword']:
  with open('/etc/shadow') as f:shadow=next((line.rstrip('\n') for line in f if line.startswith(user+':')),None)
  if shadow is None:raise RuntimeError('Cannot protect previous password: account is missing')
 with open(root+'/snapshot.json','w') as f:json.dump({'user':user,'shadow':shadow,'files':files,'expires':time.time()+payload['seconds']},f)
 with open(root+'/rollback.py','w') as f:f.write(payload['rollbackScript'])
 os.chmod(root+'/snapshot.json',0o600);os.chmod(root+'/rollback.py',0o600)
 # Arm a watchdog outside LMS before touching any live credential.
 subprocess.run(['systemd-run','--quiet','--collect','--unit='+unit,'--on-active='+str(payload['seconds'])+'s','--timer-property=AccuracySec=1s','--property=Restart=on-failure','--property=RestartSec=5s','/usr/bin/python3',root+'/rollback.py',root],check=True)
else:
 if operation=='discard' and not os.path.exists(root):
  subprocess.run(['systemctl','stop',unit+'.timer',unit+'.service'],check=False,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  sys.exit(0)
 if not os.path.isdir(root) or os.path.islink(root) or os.stat(root).st_uid!=0:raise RuntimeError('Credential recovery snapshot is missing')
 if operation=='apply':
  payload=json.load(sys.stdin)
  with open(root+'/lock','a') as lock:
   fcntl.flock(lock,fcntl.LOCK_EX)
   with open(root+'/snapshot.json') as f:saved=json.load(f)
   if os.path.exists(root+'/restored') or time.time()>=saved['expires']:raise RuntimeError('The trial expired before applying the policy')
   for path,content in [(saved['files'][0]['path'],payload['configuration']),(saved['files'][1]['path'],payload['publicKeys'])]:
    os.makedirs(os.path.dirname(path),mode=0o755,exist_ok=True)
    fd,tmp=__import__('tempfile').mkstemp(dir=os.path.dirname(path))
    try:
     with os.fdopen(fd,'w') as f:f.write(content)
     os.chmod(tmp,0o644);os.replace(tmp,path)
    finally:
     if os.path.exists(tmp):os.unlink(tmp)
   subprocess.run(['/usr/sbin/sshd','-t'],check=True,stdout=subprocess.DEVNULL)
   for service in ['ssh.service','sshd.service']:
    if subprocess.run(['systemctl','is-active','--quiet',service]).returncode==0:
     subprocess.run(['systemctl','reload',service],check=True);break
   else:raise RuntimeError('SSH service is not active')
 elif operation=='password':
  payload=json.load(sys.stdin)
  with open(root+'/lock','a') as lock:
   fcntl.flock(lock,fcntl.LOCK_EX)
   with open(root+'/snapshot.json') as f:saved=json.load(f)
   if saved['shadow'] is None or os.path.exists(root+'/restored') or time.time()>=saved['expires']:raise RuntimeError('The password trial is not active')
   password=payload['password']
   if len(password)<14 or '\n' in password or '\r' in password:raise RuntimeError('Invalid trial password')
   subprocess.run(['chpasswd'],input=user+':'+password+'\n',text=True,check=True,timeout=20,stdout=subprocess.DEVNULL)
 elif operation=='restore':
  subprocess.run(['/usr/bin/python3',root+'/rollback.py',root,'force'],check=True)
 elif operation=='keep':
  with open(root+'/lock','a') as lock:
   fcntl.flock(lock,fcntl.LOCK_EX)
   with open(root+'/snapshot.json') as f:saved=json.load(f)
   if os.path.exists(root+'/restored') or time.time()>=saved['expires']:raise RuntimeError('The trial expired and cannot be kept')
   with open(root+'/kept','w') as f:f.write('kept')
 elif operation=='discard':
  subprocess.run(['systemctl','stop',unit+'.timer',unit+'.service'],check=False,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
  shutil.rmtree(root)
 else:raise RuntimeError('Unknown recovery operation')
""";
}
