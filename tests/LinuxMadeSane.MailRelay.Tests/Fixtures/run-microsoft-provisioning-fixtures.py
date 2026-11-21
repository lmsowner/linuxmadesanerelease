# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
import argparse,json,pathlib,subprocess
parser=argparse.ArgumentParser()
parser.add_argument('--pwsh',default='pwsh')
parser.add_argument('--generated-dir',type=pathlib.Path,required=True)
args_config=parser.parse_args()
pwsh=args_config.pwsh
fixture=str(pathlib.Path(__file__).with_name('MicrosoftProvisioningFixture.ps1'))
cases=[([],0,'complete'),(['-Resume'],0,'complete'),(['-WrongTenant'],1,'tenant'),(['-OutsideScope'],1,'scope'),(['-FailExchange'],1,'exchange'),(['-FailMailbox'],1,'mailbox'),(['-FailCleanup'],0,'complete'),(['-Alias'],0,'complete'),(['-Alias','-Resume'],0,'complete'),(['-MissingMailboxId'],1,'mailbox'),(['-Existing','-Resume'],0,'complete'),(['-MissingScopeCommand'],1,'exchange-preflight'),(['-ScopeReadDenied'],1,'scope-read'),(['-ScopeCreateRejected'],1,'scope-create'),(['-EquivalentScope','-PrincipalPresent','-Resume'],0,'complete'),(['-EquivalentScope','-Alias','-Resume'],0,'complete'),(['-ScopeRace','-Resume'],0,'complete'),(['-NamedConflict'],1,'scope-conflict'),(['-BroadScope'],0,'complete'),(['-ExclusiveScope'],0,'complete'),(['-MalformedScope'],0,'complete'),(['-StringOutsideScope'],1,'scope')]
for args,exitcode,stage in cases:
 script=str(args_config.generated_dir / ('Microsoft-'+('resume' if '-Resume' in args else 'registration')+'.ps1'))
 r=subprocess.run([pwsh,'-NoLogo','-NoProfile','-File',fixture,'-ScriptPath',script,*args],capture_output=True,text=True)
 events=[json.loads(x.split('LMS_MAIL_SETUP:',1)[1]) for x in r.stdout.splitlines() if x.startswith('LMS_MAIL_SETUP:')]
 assert r.returncode==exitcode,(args,r.returncode,r.stderr[:300],events[-3:])
 assert any(e.get('kind')==stage or e.get('stage')==stage and e.get('kind')=='error' for e in events),(args,events[-3:])
 if '-Resume' in args: assert not any(e.get('stage')=='graph' for e in events)
 if '-EquivalentScope' in args or '-ScopeRace' in args: assert any(e.get('stage')=='scope-reuse' for e in events)
 if '-BroadScope' in args or '-ExclusiveScope' in args or '-MalformedScope' in args: assert not any(e.get('stage')=='scope-reuse' for e in events)
 if '-Alias' in args:
  complete=next(e for e in events if e['kind']=='complete');assert complete['senderAddress']=='primary@example.test'
 if stage in ['scope-read','scope-create','exchange-preflight','scope-conflict']:
  error=next(e for e in events if e.get('stage')==stage and e['kind']=='error');assert error['command'] in ['Get-ManagementScope','New-ManagementScope'];assert error['detail']
 print('PASS',args or ['initial'],'exit',r.returncode,stage,flush=True)
