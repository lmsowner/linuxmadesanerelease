// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Models.RdpOptimizer;
namespace LinuxMadeSane.PackageUsage;

// Compiled into both the host and the independently updated connector. Do not
// reference new host contracts here: connectors also run on older LMS hosts.
internal static class PackageUsageCollector
{
    public static async Task<JsonElement?> CollectAsync(ILinuxCommandRunner runner, CancellationToken token)
    {
        if(!OperatingSystem.IsLinux())return null;
        var result=await runner.RunAsync(new("/usr/bin/python3",["-"],true,TimeSpan.FromSeconds(40),"Read package runtime usage")
            {StandardInputBytes=Encoding.UTF8.GetBytes(Script),IsOptionalExternalTool=true},false,token);
        if(result.ExitCode!=0 || result.StandardOutput.Length>2*1024*1024)return null;
        try { using var document=JsonDocument.Parse(result.StandardOutput);return document.RootElement.Clone(); }
        catch(JsonException){return null;}
    }
    // Read only: no command arguments, environments or file contents leave the
    // host. /proc maps provides file paths, not application memory. Missing
    // permissions/tools and collection limits are explicit coverage gaps.
    internal const string Script="""
import os,glob,re,json,subprocess,datetime,ipaddress,shlex,time
paths={}; complete=True; listeners={}; network=False; ssh_starts=[]; ssh_custom=False
try:
    r=subprocess.run(['ss','-H','-lntup'],capture_output=True,text=True,timeout=5)
    network=r.returncode==0
    for line in r.stdout.splitlines()[:4096]:
        fields=line.split()
        if len(fields)<5: continue
        endpoint=fields[0]+' '+fields[4]
        address=fields[4].rsplit(':',1)[0].strip('[]').split('%',1)[0]
        try: local=ipaddress.ip_address(address).is_loopback
        except ValueError: local=False
        for pid in re.findall(r'pid=(\d+)',line): listeners.setdefault(pid,set()).add(('loopback' if local else 'non-loopback',endpoint[:256]))
    if len(r.stdout.splitlines())>4096: network=False
except (OSError,subprocess.TimeoutExpired): pass
processes=glob.glob('/proc/[0-9]*')
if len(processes)>8192: complete=False
for proc in processes[:8192]:
    pid=proc.rsplit('/',1)[-1]
    if pid==str(os.getpid()):continue
    try:
        same_root=(os.stat(proc+'/root').st_dev,os.stat(proc+'/root').st_ino)==(os.stat('/').st_dev,os.stat('/').st_ino)
        executable=os.readlink(proc+'/exe')
        executable_stat=os.stat(proc+'/exe')
        # Daemons may chroot after launch. Attribute their real host executable,
        # but never map a container's different inode onto a same-named host file.
        if not same_root:
            host_stat=os.stat(executable.removesuffix(' (deleted)'))
            if (host_stat.st_dev,host_stat.st_ino)!=(executable_stat.st_dev,executable_stat.st_ino):continue
    except FileNotFoundError: continue
    except PermissionError: complete=False;continue
    except OSError: continue
    if pid==str(os.getppid()) and executable.endswith('/sudo'):continue
    if executable=='/usr/sbin/sshd':
        try:
            fields=open(proc+'/stat').read().rsplit(')',1)[1].split()
            boot=time.time()-float(open('/proc/uptime').read().split()[0])
            ssh_starts.append(boot+int(fields[19])/os.sysconf('SC_CLK_TCK'))
            command=open(proc+'/cmdline','rb').read().replace(b'\0',b' ').decode(errors='replace')
            ssh_custom|=re.search(r'(?:^|\s)-(?:o|f)',command) is not None
        except (OSError,ValueError,IndexError):ssh_custom=True
    def add(path,kind,inode,device):
        global complete
        # No user paths, container roots or command arguments are transmitted.
        if not path.startswith(('/usr/','/bin/','/sbin/','/lib/','/lib64/')): return
        if '\t' in path or '\n' in path or len(path)>1024:return
        deleted=path.endswith(' (deleted)')
        if deleted:path=path[:-10]
        try:
            actual=os.stat(path)
            if (actual.st_ino,actual.st_dev)!=(inode,device) and not (deleted and same_root):return
        except OSError:
            if not (deleted and same_root):return
        if len(paths)>=16384 and path not in paths:complete=False;return
        entry=paths.setdefault(path,{'executable':False,'library':False,'deleted':False,'listeners':set(),'directListeners':set(),'elfClass':0})
        entry[kind]=True;entry['deleted']|=deleted
        if not deleted:
            try:
                with open(path,'rb') as binary:header=binary.read(5)
                if header[:4]==b'\x7fELF' and header[4] in (1,2):entry['elfClass']=32 if header[4]==1 else 64
            except OSError:pass
        entry['listeners'].update(listeners.get(pid,set()))
        if kind=='executable':entry['directListeners'].update(value for scope,value in listeners.get(pid,set()) if scope=='non-loopback')
    add(executable,'executable',executable_stat.st_ino,executable_stat.st_dev)
    try:
        with open(proc+'/maps') as maps:
            for i,line in enumerate(maps):
                if i>=16384:complete=False;break
                fields=line.split(None,5)
                if len(fields)==6 and fields[5].startswith('/'):
                    major,minor=(int(value,16) for value in fields[3].split(':'))
                    add(fields[5].rstrip('\n'),'library',int(fields[4]),os.makedev(major,minor))
    except FileNotFoundError: pass
    except (PermissionError,OSError):complete=False
# Resolve merged-/usr aliases against the real dpkg file lists. Ownership is
# evidence of package use, not evidence that any particular CVE is reachable.
queries={}
for path in paths:
    aliases={path}
    if path.startswith(('/bin/','/sbin/','/lib/','/lib64/')): aliases.add('/usr'+path)
    elif path.startswith(('/usr/bin/','/usr/sbin/','/usr/lib/','/usr/lib64/')):aliases.add(path[4:])
    for alias in aliases:
        if not any(c in alias for c in '*?[]'):queries.setdefault(alias,set()).add(path)
owned={}; keys=list(queries)
for offset in range(0,len(keys),128):
    try:
        r=subprocess.run(['dpkg-query','-S','--']+keys[offset:offset+128],capture_output=True,text=True,timeout=5)
        if r.returncode not in (0,1):complete=False
        for line in r.stdout.splitlines():
            if ': ' not in line:continue
            names,path=line.split(': ',1)
            if path not in queries:continue
            for name in names.split(', '):
                package=name.split(':',1)[0]
                if not re.fullmatch(r'[a-z0-9][a-z0-9+.-]*',package):continue
                row=owned.setdefault(package,{'package':package,'executables':set(),'libraries':set(),'listeners':set(),'nonLoopbackListeners':set(),'directListeners':set(),'elfClasses':set(),'deletedFiles':False})
                for original in queries[path]:
                    e=paths[original]
                    row['elfClasses'].add(0 if e['deleted'] else e['elfClass'])
                    if e['executable']:row['executables'].add(original)
                    if e['library'] and not e['executable']:row['libraries'].add(original)
                    row['directListeners'].update(e['directListeners']);row['listeners'].update(value for scope,value in e['listeners']);row['nonLoopbackListeners'].update(value for scope,value in e['listeners'] if scope=='non-loopback');row['deletedFiles']|=e['deleted']
    except (OSError,subprocess.TimeoutExpired):complete=False
rows=[]
for name,row in sorted(owned.items()):
    for key in ('executables','libraries','listeners','nonLoopbackListeners','directListeners','elfClasses'):row[key]=sorted(row[key])[:10]
    rows.append(row)
# A narrowly defined, read-only prerequisite check. Inspect every Include even
# under Match blocks: any possible enabling directive makes the result unknown.
configuration=[]
if os.path.exists('/usr/sbin/sshd'):
    value='unknown'; reason='Effective SSH tunnel configuration could not be verified.'
    try:
        effective=subprocess.run(['/usr/sbin/sshd','-T'],capture_output=True,text=True,timeout=5)
        setting=re.search(r'(?m)^permittunnel (\S+)$',effective.stdout)
        pending=['/etc/ssh/sshd_config']; seen=set(); safe=True; latest=0
        while pending:
            path=pending.pop()
            if path in seen:continue
            seen.add(path)
            if len(seen)>64 or os.path.getsize(path)>1024*1024:safe=False;break
            latest=max(latest,os.path.getmtime(path))
            with open(path) as source:
                for line in source:
                    parts=shlex.split(line,comments=True)
                    if not parts:continue
                    if '=' in parts[0]:parts=parts[0].split('=',1)+parts[1:]
                    if parts[0].lower()=='permittunnel' and (len(parts)!=2 or parts[1].lower()!='no'):safe=False
                    if parts[0].lower()=='include':
                        for pattern in parts[1:]:
                            if '%' in pattern or '~' in pattern:safe=False;continue
                            if not pattern.startswith('/'):pattern='/etc/ssh/'+pattern
                            pending.extend(glob.glob(pattern))
        if effective.returncode==0 and setting and setting[1]=='no' and safe and ssh_starts and not ssh_custom and latest<min(ssh_starts)-2:
            value='disabled-verified';reason='sshd -T reports PermitTunnel no; no enabling directive in any included configuration; configuration predates running sshd; no -f/-o process override observed.'
        elif effective.returncode==0 and setting and setting[1]!='no':
            value='enabled';reason='sshd -T reports PermitTunnel '+setting[1]+'. Per-connection prerequisites remain unverified.'
    except (OSError,ValueError,subprocess.TimeoutExpired):pass
    configuration.append({'name':'openssh.permit-tunnel','value':value,'evidence':reason})
print(json.dumps({'schemaVersion':2,'capturedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'processCoverageComplete':complete,'listenerCoverageComplete':network,'packages':rows,'configuration':configuration}))

""";
}
