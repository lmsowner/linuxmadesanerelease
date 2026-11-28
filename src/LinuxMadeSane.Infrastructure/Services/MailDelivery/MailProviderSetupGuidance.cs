// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Security.Cryptography;
using LinuxMadeSane.Core.Models.MailRelay;

namespace LinuxMadeSane.Infrastructure.Services;

public static class MailProviderSetupGuidance
{
    private static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
    public static string Microsoft(string tenant, string mailbox, string publicCertificate, string clientId = "", string servicePrincipalId = "") => $$"""
        # PowerShell 7.6+. Run as your Microsoft 365 setup administrator.
        # This uploads only the public certificate; its private key stays encrypted in LMS.
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion -lt [version]'7.6') { exit 78 }
        $ProgressPreference = 'SilentlyContinue'
        $PSStyle.OutputRendering = 'PlainText'
        function Send-MailSetupStage([string]$stage) { $script:setupStage = $stage; Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='progress'; stage=$stage } | ConvertTo-Json -Compress)) }
        function Test-MailboxOnlyScope($scope, [string]$address) {
          if (([string]$scope.Exclusive -ieq 'True') -or -not [string]::IsNullOrWhiteSpace([string]$scope.ServerFilter)) { return $false }
          # Accept only a single equality predicate, with balanced optional outer parentheses.
          # Compound filters, wildcards and exclusive/server scopes are never reused.
          $pattern = "^\s*(?<open>(?:\(\s*)*)PrimarySmtpAddress\s+-eq\s+'(?<address>(?:[^']|'')*)'\s*(?<close>(?:\)\s*)*)$"
          $match = [regex]::Match([string]$scope.RecipientFilter, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
          if (-not $match.Success) { return $false }
          if ([regex]::Matches($match.Groups['open'].Value, '\(').Count -ne [regex]::Matches($match.Groups['close'].Value, '\)').Count) { return $false }
          return $match.Groups['address'].Value.Replace("''", "'") -ieq $address
        }
        function Find-MailboxOnlyScope($scopes, [string]$address) {
          $scopes | Where-Object { Test-MailboxOnlyScope $_ $address } |
            Sort-Object @{ Expression={ -not [string]::IsNullOrWhiteSpace([string]$_.RecipientRoot) } }, Name | Select-Object -First 1
        }
        $tenant = {{Ps(tenant)}}
        $client = {{Ps(clientId)}}
        $principalId = {{Ps(servicePrincipalId)}}
        $mailbox = {{Ps(mailbox)}}
        $publicCertificate = {{Ps(publicCertificate)}}
        try {
        Send-MailSetupStage 'modules'
        # Reviewed module versions keep the two Microsoft SDKs compatible.
        foreach ($module in @(@{ Name='Microsoft.Graph.Authentication'; Version='2.41.2' }, @{ Name='ExchangeOnlineManagement'; Version='3.10.1' })) {
          if ($client -and $principalId -and $module.Name -eq 'Microsoft.Graph.Authentication') { continue }
          if (-not (Get-Module -ListAvailable $module.Name | Where-Object { $_.Version -eq [version]$module.Version })) {
            Install-Module $module.Name -RequiredVersion $module.Version -Scope CurrentUser -Repository PSGallery -Force -AllowClobber -Confirm:$false
          }
          Import-Module $module.Name -RequiredVersion $module.Version -ErrorAction Stop
        }
        $key = 'lms-mail-{{Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicCertificate)))[..16].ToLowerInvariant()}}-{{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mailbox.ToLowerInvariant())))[..8].ToLowerInvariant()}}'
        if (-not ($client -and $principalId)) {
        Send-MailSetupStage 'graph'
        $signIn = @{ Scopes='Application.ReadWrite.All'; UseDeviceAuthentication=$true; ContextScope='Process'; NoWelcome=$true }
        if ($tenant) { $signIn.TenantId = $tenant }
        Connect-MgGraph @signIn
        $tenant = ([guid](Get-MgContext).TenantId).ToString()
        Send-MailSetupStage 'application'
          $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($publicCertificate))
          $body = @{
            displayName = "LMS Mail $mailbox"; signInAudience = 'AzureADMyOrg'
            keyCredentials = @(@{ type = 'AsymmetricX509Cert'; usage = 'Verify'; key = $publicCertificate;
              startDateTime = $certificate.NotBefore.ToUniversalTime().ToString('o'); endDateTime = $certificate.NotAfter.ToUniversalTime().ToString('o') })
          }
          # Alternate-key upsert makes rerunning this script resumable.
          $null = Invoke-MgGraphRequest -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications(uniqueName='$key')" -Headers @{ Prefer='create-if-missing' } -Body ($body | ConvertTo-Json -Depth 6) -ContentType 'application/json'
          $app = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/applications(uniqueName='$key')"
          $client = $app.appId
          Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='registered'; tenantId=$tenant; clientId=$client } | ConvertTo-Json -Compress))
          $principals = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '$client'"
          if ($principals.value.Count -eq 0) {
            $principal = Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals' -Body (@{ appId = $client } | ConvertTo-Json) -ContentType 'application/json'
          } else { $principal = $principals.value[0] }
          $principalId = $principal.id
          Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='registered'; tenantId=$tenant; clientId=$client; principalId=$principalId } | ConvertTo-Json -Compress))
        }
          Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='registered'; tenantId=$tenant; clientId=$client; principalId=$principalId } | ConvertTo-Json -Compress))
          Send-MailSetupStage 'exchange'
          Connect-ExchangeOnline -Device -ShowBanner:$false
          try {
            if (-not (Get-ConnectionInformation | Where-Object { $_.TenantID -eq $tenant })) { Write-Output 'LMS_MAIL_SETUP:{"kind":"error","stage":"tenant"}'; throw 'Tenant mismatch' }
            Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='exchange-connected'; tenantId=$tenant; clientId=$client; principalId=$principalId } | ConvertTo-Json -Compress))
            Send-MailSetupStage 'exchange-preflight'
            foreach ($command in @('Get-EXOMailbox','Get-ServicePrincipal','New-ServicePrincipal','Get-ManagementScope','New-ManagementScope','Get-ManagementRoleAssignment','New-ManagementRoleAssignment','Test-ServicePrincipalAuthorization')) {
              $script:setupCommand = $command
              if (-not (Get-Command -Name $command -ErrorAction SilentlyContinue)) {
                throw [System.Management.Automation.CommandNotFoundException]::new("This Exchange session does not expose $command. The signed-in account needs Exchange Role Management permissions for this setup.")
              }
            }
            Send-MailSetupStage 'mailbox'
            $script:setupCommand = 'Get-EXOMailbox'
            $senderMailbox = Get-EXOMailbox -Identity $mailbox -Properties PrimarySmtpAddress,ExternalDirectoryObjectId -ErrorAction Stop
            $mailbox = [string]$senderMailbox.PrimarySmtpAddress
            $mailboxId = [string]$senderMailbox.ExternalDirectoryObjectId
            if ([string]::IsNullOrWhiteSpace($mailbox) -or [guid]$mailboxId -eq [guid]::Empty) { throw 'Mailbox identity missing' }
            Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='mailbox-resolved'; tenantId=$tenant; clientId=$client; principalId=$principalId; senderAddress=$mailbox; mailboxId=$mailboxId } | ConvertTo-Json -Compress))
            Send-MailSetupStage 'principal-read'
            $script:setupCommand = 'Get-ServicePrincipal'
            $existingPrincipal = @(Get-ServicePrincipal -ErrorAction Stop | Where-Object { $_.ObjectId -eq $principalId -and $_.AppId -eq $client })
            if ($existingPrincipal.Count -eq 0) {
              Send-MailSetupStage 'principal'
              $script:setupCommand = 'New-ServicePrincipal'
              $null = New-ServicePrincipal -AppId $client -ObjectId $principalId -DisplayName "LMS Mail $mailbox"
            }
            $scopeName = "$key-sender"
            $escapedMailbox = $mailbox.Replace("'", "''")
            Send-MailSetupStage 'scope-read'
            $script:setupCommand = 'Get-ManagementScope'
            $scopes = @(Get-ManagementScope -ErrorAction Stop)
            $namedScope = @($scopes | Where-Object { $_.Name -eq $scopeName })
            if ($namedScope.Count -gt 0 -and -not (Test-MailboxOnlyScope $namedScope[0] $mailbox)) {
              Send-MailSetupStage 'scope-conflict'
              throw 'The named scope has a different or broader restriction. LMS has not changed it or assigned a sending role to it.'
            }
            $matchingScope = if ($namedScope.Count -gt 0) { $namedScope[0] } else { Find-MailboxOnlyScope $scopes $mailbox }
            if ($null -ne $matchingScope) {
              $scopeName = [string]$matchingScope.Name
              Send-MailSetupStage 'scope-reuse'
            } else {
              Send-MailSetupStage 'scope-create'
              $script:setupCommand = 'New-ManagementScope'
              try { $null = New-ManagementScope -Name $scopeName -RecipientRestrictionFilter "PrimarySmtpAddress -eq '$escapedMailbox'" }
              catch {
                # A concurrent setup can create an equivalent scope after our read.
                $creationFailure = $_
                Send-MailSetupStage 'scope-read'
                $script:setupCommand = 'Get-ManagementScope'
                $matchingScope = Find-MailboxOnlyScope @(Get-ManagementScope -ErrorAction Stop) $mailbox
                if ($null -eq $matchingScope) {
                  Send-MailSetupStage 'scope-create'
                  $script:setupCommand = 'New-ManagementScope'
                  throw $creationFailure
                }
                $scopeName = [string]$matchingScope.Name
                Send-MailSetupStage 'scope-reuse'
              }
            }
            Send-MailSetupStage 'role-read'
            $script:setupCommand = 'Get-ManagementRoleAssignment'
            $existingAssignment = @(Get-ManagementRoleAssignment -ErrorAction Stop | Where-Object { $_.Name -eq "$key-send" })
            if ($existingAssignment.Count -eq 0) {
              Send-MailSetupStage 'role'
              $script:setupCommand = 'New-ManagementRoleAssignment'
              $null = New-ManagementRoleAssignment -Name "$key-send" -Role 'Application Mail.Send' -App $principalId -CustomResourceScope $scopeName
            }
            Send-MailSetupStage 'verify'
            $script:setupCommand = 'Test-ServicePrincipalAuthorization'
            $authorisation = @(Test-ServicePrincipalAuthorization -Identity $principalId -Resource $mailbox)
            if (-not ($authorisation | Where-Object { $_.RoleName -eq 'Application Mail.Send' -and ($_.InScope -eq $true) })) { Write-Output 'LMS_MAIL_SETUP:{"kind":"error","stage":"scope"}'; throw 'Scope not confirmed' }
            # No tenant-wide Graph Mail.Send grant: Entra grants and Exchange RBAC are additive.
            Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='complete'; tenantId=$tenant; clientId=$client; principalId=$principalId; senderAddress=$mailbox; mailboxId=$mailboxId } | ConvertTo-Json -Compress))
          } finally { try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue } catch { } }
        } catch {
          $detail = [string]$_.Exception.Message
          if ($detail.Length -gt 4096) { $detail = $detail.Substring(0,4096) }
          Write-Output ('LMS_MAIL_SETUP:' + (@{ kind='error'; stage=$script:setupStage; command=$script:setupCommand; category=[string]$_.CategoryInfo.Category; detail=$detail } | ConvertTo-Json -Compress))
          exit 1
        } finally { try { if (Get-Command Disconnect-MgGraph -ErrorAction SilentlyContinue) { Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null } } catch { } }
        """;

}
