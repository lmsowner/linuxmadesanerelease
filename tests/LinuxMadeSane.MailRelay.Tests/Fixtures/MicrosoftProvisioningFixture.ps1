# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE for details.
param([Parameter(Mandatory=$true)][string]$ScriptPath,[switch]$WrongTenant,[switch]$OutsideScope,[switch]$Resume,[switch]$FailExchange,[switch]$FailMailbox,[switch]$FailCleanup,[switch]$Alias,[switch]$MissingMailboxId,[switch]$Existing,[switch]$MissingScopeCommand,[switch]$ScopeReadDenied,[switch]$ScopeCreateRejected,[switch]$EquivalentScope,[switch]$PrincipalPresent,[switch]$ScopeRace,[switch]$NamedConflict,[switch]$BroadScope,[switch]$ExclusiveScope,[switch]$MalformedScope,[switch]$StringOutsideScope)
$ErrorActionPreference='Stop'
$fixtureTenant='d3333333-3333-3333-3333-333333333333'
$fixtureClient='a1111111-1111-1111-1111-111111111111'
function Get-Module { param([switch]$ListAvailable,[Parameter(Position=0)]$Name) @{Name=$Name;Version=$(if($Name -eq 'ExchangeOnlineManagement'){'3.10.1'}else{'2.41.2'})} }
function Import-Module { param([Parameter(Position=0)]$Name,$RequiredVersion) }
function Install-Module { throw 'No live module installation in fixtures' }
function Connect-MgGraph { param($Scopes,[switch]$UseDeviceAuthentication,$ContextScope,[switch]$NoWelcome) if($Resume){throw 'Repeated Graph authorisation on resume'}; if($ContextScope -ne 'Process'){throw 'Unexpected auth cache'}; Write-Warning 'To sign in, open https://microsoft.com/devicelogin and enter the code ABCD12345' }
function Get-MgContext { @{TenantId=$fixtureTenant} }
function Invoke-MgGraphRequest { param($Method,$Uri,$Body,$ContentType,$Headers)
 if($Uri.Contains('servicePrincipals?')) { return @{value=@()} }
 if($Uri.EndsWith('servicePrincipals')) { return @{id='e1111111-1111-1111-1111-111111111111'} }
 if($Method -eq 'PATCH' -and $Headers.Prefer -ne 'create-if-missing') {throw 'Unsafe creation'}
 return @{appId=$fixtureClient}
}
function Connect-ExchangeOnline { param([switch]$Device,$ShowBanner) if($FailExchange){throw 'Private SDK exception should stay out of UI'}; if(-not $Device){throw 'Must use device auth'} }
function Get-ConnectionInformation { @{TenantID=$(if($WrongTenant){'ffffffff-ffff-ffff-ffff-ffffffffffff'}else{$fixtureTenant})} }
function Get-EXOMailbox { [CmdletBinding()] param($Identity,$Properties) if($Properties -notcontains "ExternalDirectoryObjectId"){throw "Missing mailbox metadata request"}; if($FailMailbox){throw 'Mailbox failure'}; @{PrimarySmtpAddress=$(if($Alias){"primary@example.test"}else{$Identity});ExternalDirectoryObjectId=$(if($MissingMailboxId){""}else{"f1111111-1111-1111-1111-111111111111"})} }
function Get-ServicePrincipal { [CmdletBinding()] param($Identity) if($PSBoundParameters.ContainsKey('Identity')){throw 'Unexpected identity lookup'}; if($Existing -or $PrincipalPresent){@{ObjectId='e1111111-1111-1111-1111-111111111111';AppId=$fixtureClient}}else{@{ObjectId='ffffffff-ffff-ffff-ffff-ffffffffffff';AppId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'}} }
function New-ServicePrincipal { [CmdletBinding()] param($AppId,$ObjectId,$DisplayName) if($AppId -ne $fixtureClient -or $ObjectId -ne 'e1111111-1111-1111-1111-111111111111'){throw 'Unexpected application identity'}; if($Existing -or $PrincipalPresent){throw 'Existing application recreated'} }
function Get-ManagementScope { [CmdletBinding()] param($Identity)
 if($PSBoundParameters.ContainsKey('Identity')){throw 'Identity lookup for missing scope terminates'}
 if($ScopeReadDenied){throw [System.UnauthorizedAccessException]::new('Exchange scope listing denied by tenant policy')}
 if($Existing){return @{Name="$key-sender";RecipientFilter="PrimarySmtpAddress -eq '$mailbox'";Exclusive=$false}}
 if($NamedConflict){return @{Name="$key-sender";RecipientFilter="PrimarySmtpAddress -eq '$mailbox' -or PrimarySmtpAddress -eq 'other@example.test'";Exclusive=$false}}
 if($EquivalentScope -or ($ScopeRace -and $script:raceCreated)){return @{Name='existing-mailbox-scope';RecipientFilter=" ( ( PRIMARYSMTPADDRESS  -eq '$mailbox' ) ) ";Exclusive=$false}}
 if($BroadScope){return @{Name='existing-mailbox-scope';RecipientFilter="PrimarySmtpAddress -eq '$mailbox' -or PrimarySmtpAddress -eq 'other@example.test'";Exclusive=$false}}
 if($ExclusiveScope){return @{Name='existing-mailbox-scope';RecipientFilter="PrimarySmtpAddress -eq '$mailbox'";Exclusive=$true}}
 if($MalformedScope){return @{Name='existing-mailbox-scope';RecipientFilter="((PrimarySmtpAddress -eq '$mailbox')";Exclusive=$false}}
 @{Name='unrelated-existing-scope';RecipientFilter="PrimarySmtpAddress -eq 'other@example.test'";Exclusive=$false}
}
function New-ManagementScope { [CmdletBinding()] param($Name,$RecipientRestrictionFilter) if($Existing -or $EquivalentScope){throw 'Equivalent scope already exists; use the existing management scope'}; if($ScopeRace){$script:raceCreated=$true;throw 'An equivalent scope was created concurrently'}; if($ScopeCreateRejected){throw 'Exchange rejected scope definition: fixture server response'}; if($RecipientRestrictionFilter -notin @("PrimarySmtpAddress -eq 'sender@example.test'", "PrimarySmtpAddress -eq 'test@example.test'", "PrimarySmtpAddress -eq 'primary@example.test'")){throw 'Wrong scope'} }
function Get-ManagementRoleAssignment { [CmdletBinding()] param($Identity) if($PSBoundParameters.ContainsKey('Identity')){throw 'Unexpected role identity lookup'}; if($Existing){@{Name="$key-send"}}else{@{Name='unrelated-existing-role'}} }
function New-ManagementRoleAssignment { [CmdletBinding()] param($Name,$Role,$App,$CustomResourceScope) if($App -ne 'e1111111-1111-1111-1111-111111111111'){throw 'Role assigned to a different application'}; if($Existing){throw 'Existing role assignment recreated'}; $script:assignmentCreated=$true; if(($EquivalentScope -or $ScopeRace) -and $CustomResourceScope -ne 'existing-mailbox-scope'){throw 'Role was not bound to the existing equivalent scope'}; if(($BroadScope -or $ExclusiveScope -or $MalformedScope) -and $CustomResourceScope -eq 'existing-mailbox-scope'){throw 'Unsafe scope reused'}; if($Role -ne 'Application Mail.Send' -or -not $CustomResourceScope){throw 'Unscoped grant'} }
function Test-ServicePrincipalAuthorization { [CmdletBinding()] param($Identity,$Resource) if(-not ($script:assignmentCreated -or $Existing)){throw "Permission assignment never executed"}; if($Alias -and $Resource -ne "primary@example.test"){throw "Unresolved alias used for mailbox authorisation"}; @{RoleName='Application Mail.Send';InScope=$(if($StringOutsideScope){"False"}else{-not $OutsideScope})} }
function Disconnect-ExchangeOnline { param($Confirm) if($FailCleanup){throw 'Cleanup failure'} }
function Disconnect-MgGraph { }
if($MissingScopeCommand){ Remove-Item function:New-ManagementScope }
. $ScriptPath
if($LASTEXITCODE){exit $LASTEXITCODE}
