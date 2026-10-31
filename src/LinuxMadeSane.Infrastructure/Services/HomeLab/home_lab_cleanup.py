# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
"""Runs inside a private mount namespace; never follows symlinks or crosses mounts."""
import ctypes, datetime, json, os, re, stat, subprocess, sys, uuid
LOCAL_FILESYSTEMS = {'ext2','ext3','ext4','xfs','btrfs','zfs','tmpfs','overlay','f2fs'}
NAME = re.compile(r'^lms-homelab-[a-z0-9][a-z0-9-]*-[a-f0-9]{8}$')
FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC

def mount_id(fd):
    with open('/proc/self/fdinfo/'+str(fd)) as f:
        for line in f:
            if line.startswith('mnt_id:'): return int(line.split()[1])
    raise RuntimeError('Cannot verify mount identity')

def mounts():
    result={}
    with open('/proc/self/mountinfo') as f:
        for line in f:
            fields=line.split();sep=fields.index('-');result[int(fields[0])]=fields[sep+1]
    return result

def open_path(path):
    # No component, including an ancestor of the HomeLab root, may be a link.
    if not os.path.isabs(path) or os.path.normpath(path)!=path or path=='/': raise ValueError('Invalid cleanup root')
    fd=os.open('/',FLAGS)
    try:
        for name in path.strip('/').split('/'):
            child=os.open(name,FLAGS,dir_fd=fd);os.close(fd);fd=child
        return fd
    except BaseException:
        os.close(fd);raise

def verify_tree(fd,expected):
    if mount_id(fd)!=expected: raise ValueError('Mounted folder preserved')
    for name in os.listdir(fd):
        value=os.stat(name,dir_fd=fd,follow_symlinks=False)
        if stat.S_ISDIR(value.st_mode):
            child=os.open(name,FLAGS,dir_fd=fd)
            try: verify_tree(child,expected)
            finally: os.close(child)
        elif stat.S_ISREG(value.st_mode):
            child=os.open(name,os.O_PATH|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=fd)
            try:
                if mount_id(child)!=expected: raise ValueError('Mounted file preserved')
            finally: os.close(child)
        elif not stat.S_ISLNK(value.st_mode):
            raise ValueError('Special filesystem entry preserved')

HOLDING = '.lms-retained'
MARKER = b'LMS recoverable HomeLab storage v1\n'

def rename_new(source_fd, source, target_fd, target):
    # Atomic no-overwrite move; never copy or recursively delete app contents.
    libc=ctypes.CDLL(None,use_errno=True)
    if libc.renameat2(source_fd,source.encode(),target_fd,target.encode(),1)!=0:
        error=ctypes.get_errno();raise OSError(error,os.strerror(error))

def holding(root_fd, mid, create=False):
    if create:
        try:
            os.mkdir(HOLDING,0o700,dir_fd=root_fd)
            fd=os.open(HOLDING,FLAGS,dir_fd=root_fd)
            try:
                marker=os.open('.lms-owned',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=fd)
                try: os.write(marker,MARKER);os.fsync(marker)
                finally: os.close(marker)
            finally: os.close(fd)
        except FileExistsError: pass
    fd=os.open(HOLDING,FLAGS,dir_fd=root_fd)
    try:
        if mount_id(fd)!=mid: raise ValueError('Holding area is mounted; preserved')
        marker=os.open('.lms-owned',os.O_RDONLY|os.O_NOFOLLOW,dir_fd=fd)
        try:
            if os.read(marker,1024)!=MARKER: raise ValueError('Unrecognised holding area')
        finally: os.close(marker)
        return fd
    except BaseException: os.close(fd);raise

def archive(root_fd, name, mid):
    retained=holding(root_fd,mid,True);receipt=uuid.uuid4().hex
    try:
        os.mkdir(receipt,0o700,dir_fd=retained);fd=os.open(receipt,FLAGS,dir_fd=retained)
        try:
            meta=os.open('metadata.json',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=fd)
            try:
                os.write(meta,json.dumps({'name':name,'archivedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()}).encode());os.fsync(meta)
            finally: os.close(meta)
            rename_new(root_fd,name,fd,'files');os.fsync(fd);os.fsync(root_fd)
        finally: os.close(fd)
    finally: os.close(retained)
    return receipt

def archived(plan, root_fd, mid):
    try: retained=holding(root_fd,mid)
    except FileNotFoundError: return []
    result=[]
    try:
        for receipt in sorted(os.listdir(retained)):
            if not re.fullmatch('[a-f0-9]{32}',receipt): continue
            fd=os.open(receipt,FLAGS,dir_fd=retained)
            try:
                if mount_id(fd)!=mid: raise ValueError('Mounted receipt preserved')
                meta=os.open('metadata.json',os.O_RDONLY|os.O_NOFOLLOW,dir_fd=fd)
                try: name=json.loads(os.read(meta,4096))['name']
                finally: os.close(meta)
                if not NAME.fullmatch(name): raise ValueError('Invalid retained app name')
                try: files=os.open('files',FLAGS,dir_fd=fd)
                except FileNotFoundError: continue
                item={'name':name,'path':plan['root']+'/'+HOLDING+'/'+receipt+'/files','archiveId':receipt,'removable':False,'removed':False,'reason':'Retained; available to restore'}
                result.append(item)
                try:
                    verify_tree(files,mid)
                    if plan.get('mode')=='restore' and receipt in plan.get('selected',[]):
                        rename_new(fd,'files',root_fd,name);os.fsync(root_fd);os.fsync(fd)
                        item.update(removed=True,reason='Restored to original folder')
                except (OSError,ValueError) as error: item['reason']='Preserved: '+str(error)
                finally: os.close(files)
            finally: os.close(fd)
    finally: os.close(retained)
    return result

def related(a,b):
    return a==b or a.startswith(b.rstrip('/')+'/') or b.startswith(a.rstrip('/')+'/')

def docker_usage():
    ids=subprocess.run(['docker','ps','-aq'],capture_output=True,text=True,check=True,timeout=30).stdout.split()
    containers=[]
    for offset in range(0,len(ids),100):
        containers+=json.loads(subprocess.run(['docker','inspect',*ids[offset:offset+100]],capture_output=True,text=True,check=True,timeout=30).stdout)
    names=set();paths=[]
    for c in containers:
        names.add(c['Name'].lstrip('/'))
        managed=c.get('Config',{}).get('Labels',{}).get('com.linuxmadesane.homelab')=='true'
        for binding in c.get('Mounts',[]):
            source=binding.get('Source')
            if not source: continue
            if managed and source=='/mnt/storage' and binding.get('Destination')=='/storage': continue
            paths.append(os.path.normpath(source))
    return names,paths

def process(plan):
    root=plan['root'];result=[]
    try: root_fd=open_path(root)
    except FileNotFoundError: return []
    try:
        mid=mount_id(root_fd)
        parent=os.path.dirname(root)
        parent_fd=os.open('/',FLAGS) if parent=='/' else open_path(parent)
        try:
            if mount_id(parent_fd)!=mid: raise ValueError('HomeLab root is a mount point; preserved')
        finally: os.close(parent_fd)
        if mounts().get(mid) not in LOCAL_FILESYSTEMS: raise ValueError('Network or unverified filesystem preserved')
        if plan.get('mode') in {'archived','restore'}: return archived(plan,root_fd,mid)
        fstab=[]
        with open('/etc/fstab') as file:
            for line in file:
                fields=line.strip().split()
                if fields and not fields[0].startswith('#') and len(fields)>=3 and fields[1].startswith('/'):
                    target=re.sub(r'\\([0-7]{3})',lambda m:chr(int(m.group(1),8)),fields[1])
                    fstab.append((os.path.normpath(target),fields[2]))
        names,used=docker_usage()
        protected=plan.get('protectedPaths',[])+used
        known=set(plan.get('registeredNames',[]))|names
        selected=set(plan.get('selected',[]))
        entries=sorted(os.listdir(root_fd))
        for name in entries:
            if '/' in name or name in {'.','..'}: raise ValueError('Invalid entry name')
            path=root+'/'+name;item={'name':name,'path':path,'removable':False,'reason':'Not an LMS app folder','removed':False}
            result.append(item)
            if not NAME.fullmatch(name): continue
            if name in known: item['reason']='Used by a registered or existing container (including stopped containers)';continue
            if any(related(path,p) or related(os.path.realpath(path),os.path.realpath(p)) for p in protected): item['reason']='Shared storage, media or container path preserved';continue
            if any(target==path or target.startswith(path+'/') or kind not in LOCAL_FILESYSTEMS and kind!='swap' and path.startswith(target.rstrip('/')+'/') for target,kind in fstab):
                item['reason']='Configured filesystem mount preserved';continue
            try:
                info=os.stat(name,dir_fd=root_fd,follow_symlinks=False)
                if not stat.S_ISDIR(info.st_mode): item['reason']='Linked or non-directory entry preserved';continue
                fd=os.open(name,FLAGS,dir_fd=root_fd)
                try:
                    verify_tree(fd,mid)
                    item.update(removable=True,reason='Unused LMS app folder; external link targets stay untouched')
                    if plan.get('apply') and name in selected:
                        # Check Docker again immediately before each removal.
                        live_names,live_paths=docker_usage()
                        if name in live_names or any(related(path,p) for p in live_paths):
                            item.update(removable=False,reason='Now in use; preserved');continue
                        # The open descriptor pins the checked directory. A replaced
                        # pathname cannot redirect traversal to a different tree.
                        now=os.stat(name,dir_fd=root_fd,follow_symlinks=False)
                        if (now.st_dev,now.st_ino)!=(info.st_dev,info.st_ino): raise ValueError('Folder changed; rescan required')
                        item.update(archiveId=archive(root_fd,name,mid),removed=True,reason='Moved to recoverable holding area; no files deleted')
                finally: os.close(fd)
            except (OSError,ValueError) as error: item.update(removable=False,reason=str(error))
        return result
    finally: os.close(root_fd)

if __name__=='__main__':
    try: print(json.dumps(process(json.load(sys.stdin))))
    except Exception as error:
        print('Cleanup stopped: '+type(error).__name__+': '+str(error),file=sys.stderr);sys.exit(1)
