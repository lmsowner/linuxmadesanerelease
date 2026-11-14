// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using LinuxMadeSane.Application.Vulnerabilities;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models.RdpOptimizer;
namespace LinuxMadeSane.Infrastructure.Services.Vulnerabilities;
public sealed class TrivyInstaller(ILinuxCommandRunner runner,IPackageManagementService packages,TrivyEngine engine)
{
    public async Task ManageAsync(string action,CancellationToken token)
    {
        if(action is not ("install" or "upgrade" or "remove"))throw new ArgumentException("Unknown scanner action.");
        var detected=await engine.ExecutableAsync(token);
        if(action=="install"&&detected is not null)return;
        if(action=="remove"&&detected!=TrivyEngine.ManagedExecutable)
        {
            var installed=await packages.InspectAsync(["trivy"],token);
            if(installed.Any(p=>p.IsInstalled))
            {
                var logs=await packages.ApplyActionsAsync([new(PackageActionKind.Remove,"trivy","Remove optional scanner; keep scan history",false,"")],false,token);
                if(logs.Any(l=>l.Level==OperationLogLevel.Error))throw new InvalidOperationException("Package-manager removal of Trivy failed.");
                return;
            }
            throw new InvalidOperationException("This Trivy installation is externally managed. Remove it using its original installer.");
        }
        if(action=="upgrade"&&detected is not null&&detected!=TrivyEngine.ManagedExecutable)
        {
            var installed=await packages.InspectAsync(["trivy"],token);
            if(!installed.Any(p=>p.IsInstalled))throw new InvalidOperationException("Update the existing Trivy installation using its original installer; LMS will not install a duplicate.");
            var logs=await packages.ApplyActionsAsync([new(PackageActionKind.Install,"trivy","Upgrade the existing package-managed scanner",false,"")],false,token);
            if(logs.Any(l=>l.Level==OperationLogLevel.Error))throw new InvalidOperationException("Package-manager upgrade of Trivy failed.");
            return;
        }
        var dependencies=await packages.InspectAsync(["python3"],token);
        if(!dependencies.Any(p=>p.IsInstalled))
        {
            var logs=await packages.ApplyActionsAsync([new(PackageActionKind.Install,"python3","Verify official scanner release",false,"")],false,token);
            if(logs.Any(l=>l.Level==OperationLogLevel.Error))throw new InvalidOperationException("Could not install scanner verification prerequisites.");
        }
        var script=action=="remove"?RemoveScript:InstallScript;
        var result=await runner.RunAsync(new("python3",["-c",script],true,TimeSpan.FromMinutes(8),"Manage optional Trivy scanner"),false,token);
        if(result.ExitCode!=0)throw new InvalidOperationException("Trivy installation operation failed: "+result.StandardError[..Math.Min(1000,result.StandardError.Length)]);
    }
    public const string InstallScript="""
import os,platform,urllib.request,hashlib,tarfile,tempfile,pathlib,shutil
version='0.76.1'
assets={'x86_64':('Linux-64bit','c7cbe3137fb8a48a17574bede3dd6beb16742d1e9772929940fc90082c39d9c0'),'aarch64':('Linux-ARM64','e174dff2a6dba5e3ae41f8d0537a6324405bd08132497340304c6b20e0f16a1f')}
arch=platform.machine()
if arch not in assets: raise RuntimeError('Official Trivy binary unavailable for this architecture; existing LMS functionality is unchanged')
asset,digest=assets[arch]
root=pathlib.Path('/opt/linuxmadesane/trivy');root.mkdir(parents=True,exist_ok=True)
if root.is_symlink(): raise RuntimeError('Unexpected scanner installation symlink')
release=root/('v'+version);release.mkdir(exist_ok=True)
if release.is_symlink(): raise RuntimeError('Unexpected scanner version symlink')
with tempfile.TemporaryDirectory(prefix='lms-trivy-') as temp:
 archive=pathlib.Path(temp)/'trivy.tar.gz'
 with urllib.request.urlopen('https://github.com/aquasecurity/trivy/releases/download/v'+version+'/trivy_'+version+'_'+asset+'.tar.gz',timeout=90) as src,archive.open('wb') as dst:
  size=0
  while True:
   data=src.read(1048576)
   if not data: break
   size+=len(data)
   if size>160*1024*1024: raise RuntimeError('Scanner archive too large')
   dst.write(data)
 if hashlib.sha256(archive.read_bytes()).hexdigest()!=digest: raise RuntimeError('Official Trivy SHA256 verification failed')
 with tarfile.open(archive) as tar:
  for name in ['trivy','LICENSE','NOTICE']:
   try: member=tar.getmember(name)
   except KeyError:
    if name=='NOTICE': continue
    raise
   if not member.isfile() or member.size>300*1024*1024: raise RuntimeError('Invalid release member')
   target=release/(name+'.new')
   if target.is_symlink(): raise RuntimeError('Unexpected release member symlink')
   with tar.extractfile(member) as src,target.open('wb') as dst: shutil.copyfileobj(src,dst)
   target.chmod(0o755 if name=='trivy' else 0o644);os.replace(target,release/name)
 link=root/'current.new'
 if link.is_symlink():link.unlink()
 link.symlink_to(release);os.replace(link,root/'current')
""";
    public const string RemoveScript="""
import os,pathlib
root=pathlib.Path('/opt/linuxmadesane/trivy');current=root/'current'
if root.is_symlink() or not current.is_symlink():raise RuntimeError('Scanner installation is not LMS managed')
release=current.resolve()
if release.parent!=root or os.path.ismount(release):raise RuntimeError('Unexpected scanner installation path')
for name in ['trivy','LICENSE','NOTICE']:
 p=release/name
 if p.is_symlink():raise RuntimeError('Unexpected scanner member')
current.unlink()
for name in ['trivy','LICENSE','NOTICE']:(release/name).unlink(missing_ok=True)
release.rmdir()
# Cache and finding history are intentionally preserved.
""";
}
