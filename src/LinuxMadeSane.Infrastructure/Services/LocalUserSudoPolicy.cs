// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Infrastructure.Services;

internal static class LocalUserSudoPolicy
{
    internal const string Script = """
import os,sys,re,grp,pwd,tempfile,subprocess,glob,fcntl
root='/etc/sudoers.d'
prefix='zz-linuxmadesane-user-'
operation=sys.argv[1]
if operation=='list':
 for path in glob.glob(root+'/'+prefix+'*'):
  if os.path.islink(path):raise RuntimeError('Unsafe managed sudo policy')
  with open(path) as f:content=f.read()
  user=os.path.basename(path)[len(prefix):]
  # Filenames are encoded so dots in usernames do not make sudo ignore the file.
  user=bytes.fromhex(user).decode()
  if 'NOPASSWD: ALL' in content:print(user+'\tPasswordless')
  elif 'PASSWD: ALL' in content:print(user+'\tPasswordRequired')
 sys.exit(0)
user,mode=sys.argv[2:4]
if not re.fullmatch('[a-zA-Z_][a-zA-Z0-9_.-]*[$]?',user):raise RuntimeError('Invalid Linux username')
if mode not in ['None','PasswordRequired','Passwordless','delete']:raise RuntimeError('Invalid sudo option')
path=root+'/'+prefix+user.encode().hex()
os.makedirs(root,mode=0o750,exist_ok=True)
with open(root+'/.linuxmadesane-users.lock','a') as lock:
 fcntl.flock(lock,fcntl.LOCK_EX)
 if os.path.islink(path):raise RuntimeError('Unsafe managed sudo policy')
 if mode=='delete':
  if os.path.exists(path):os.unlink(path)
  sys.exit(0)
 if os.geteuid()!=0:raise RuntimeError('Root privileges are required to set sudo access')
 account=pwd.getpwnam(user)
 if account.pw_uid==0:raise RuntimeError('Root sudo settings cannot be changed here')
 admin={'sudo','wheel'}
 primary=grp.getgrgid(account.pw_gid).gr_name
 oldgroups=[g.gr_name for g in grp.getgrall() if user in g.gr_mem]
 if mode=='None' and primary in admin:raise RuntimeError('Change the primary group before removing sudo access')
 groups=[g for g in oldgroups if g not in admin]
 if mode!='None' and primary not in admin:
  available=[g.gr_name for g in grp.getgrall() if g.gr_name in admin]
  if not available:raise RuntimeError('This host has no sudo or wheel administrator group')
  groups.append('sudo' if 'sudo' in available else 'wheel')
 os.makedirs(root,mode=0o750,exist_ok=True)
 previous=None
 if os.path.exists(path):
  with open(path,'rb') as f:previous=f.read()
 subprocess.run(['visudo','-c'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
 def write(content):
  fd,tmp=tempfile.mkstemp(dir=root,prefix='.lms-sudo-')
  try:
   with os.fdopen(fd,'wb') as f:f.write(content)
   os.chmod(tmp,0o440)
   subprocess.run(['visudo','-c','-f',tmp],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
   os.replace(tmp,path)
  finally:
   if os.path.exists(tmp):os.unlink(tmp)
 # Validate before changing group membership or replacing a live rule.
 content=('# Managed by Linux Made Sane\n'+user+' ALL=(ALL:ALL) '+('NOPASSWD' if mode=='Passwordless' else 'PASSWD')+': ALL\n').encode()
 changed=False
 try:
  if mode!='None':write(content)
  elif os.path.exists(path):os.unlink(path)
  changed=True
  subprocess.run(['visudo','-c'],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
  subprocess.run(['usermod','-G',','.join(groups),user],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
 except Exception:
  if changed:
   if previous is None:
    if os.path.exists(path):os.unlink(path)
   else:write(previous)
  raise
""";
}
