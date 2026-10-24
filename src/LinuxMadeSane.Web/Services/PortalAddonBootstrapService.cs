// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.

// Copyright (c) 2026 LMS Developer. All rights reserved.
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxMadeSane.Application.Interfaces;
using LinuxMadeSane.Core.Abstractions;
using LinuxMadeSane.Core.Abstractions.Portal;
using LinuxMadeSane.Core.Models.Portal;
using LinuxMadeSane.Core.Versioning;
using LinuxMadeSane.Infrastructure.Services.Infrastructure;

namespace LinuxMadeSane.Web.Services;

public sealed class PortalAddonBootstrapService(IPortalConnectionStore store, IInstanceIdentityService identities,
    ISecretStore secrets, InfrastructureHostPaths paths, IConfiguration configuration, IHttpClientFactory? clients = null)
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const long MaximumArchiveSize = 128 * 1024 * 1024;
    public static string PluginDirectory(IConfiguration config, string contentRoot) => Path.GetFullPath(config["PortalAddons:PluginDirectory"] ??
        Path.Combine(Path.GetDirectoryName(config["DataProtection:KeyDirectory"] ?? Path.Combine(contentRoot,"data","protection-keys"))!,"plugins"));
    public string PortalUrl => (configuration["PortalAddons:PortalUrl"] ?? "https://portal.linuxmadesane.com").TrimEnd('/');
    public async Task<PortalConnectionSettings?> StatusAsync(CancellationToken token = default) => await store.GetAsync(token);

    public async Task<RemotePortalRegistrationStatus> DescribeRegistrationAsync(CancellationToken token = default)
    {
        await InstallLock.WaitAsync(token);
        try
        {
            var settings = await EnsureSettingsAsync(token);
            return new(settings.LocalInstanceId, settings.PortalOrganizationId, settings.PortalInstanceId,
                settings.IsEnabled && settings.PortalInstanceId.HasValue && !IsInstallationPending(settings), settings.LastConnectionStatus);
        }
        finally { InstallLock.Release(); }
    }

    private static bool IsInstallationPending(PortalConnectionSettings settings) =>
        settings.LastConnectionStatus.StartsWith("Portal registration accepted. Installing connector",StringComparison.Ordinal);

    private async Task<PortalConnectionSettings> EnsureSettingsAsync(CancellationToken token)
    {
        var settings = await store.GetAsync(token);
        if(settings is not null) return settings;
        var now = DateTimeOffset.UtcNow;
        settings = new(1,Guid.NewGuid(),PortalUrl,Environment.MachineName,false,null,"","","",null,null,null,null,null,null,null,"Not registered.",null,null,now,now);
        await store.SaveAsync(settings,token);
        return settings;
    }

    public async Task<string> IssueManagedRegistrationCodeAsync(Guid targetLocalId, CancellationToken token = default)
    {
        var settings = await store.GetAsync(token);
        if(settings is not { IsEnabled:true, PortalInstanceId:not null, PortalOrganizationId:not null })
            throw new InvalidOperationException("Register this LMS instance with Portal first.");
        if(!Uri.TryCreate(settings.PortalBaseUrl,UriKind.Absolute,out var origin) || origin.Scheme!="https" || origin.AbsolutePath!="/" || origin.UserInfo.Length>0 ||
            !string.Equals(origin.GetLeftPart(UriPartial.Authority),PortalUrl,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved Portal registration origin does not match the configured Portal.");
        var secret=await secrets.ResolveSecretAsync(settings.PortalApiSecretReference!,token) ?? throw new InvalidOperationException("Portal credentials are unavailable.");
        using var http=CreateClient(origin);
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/portal/managed-registration-code") { Content=JsonContent.Create(new PortalManagedRegistrationRequest(targetLocalId)) };
        request.Headers.Add("X-LMS-Portal-Instance-Id",settings.PortalInstanceId.ToString());
        request.Headers.Add("X-LMS-Portal-Api-Key-Id",settings.PortalApiKeyId);
        request.Headers.Add("X-LMS-Portal-Api-Secret",secret);
        using var response=await http.SendAsync(request,token);
        if(!response.IsSuccessStatusCode) throw new InvalidOperationException("Portal could not authorise managed-host registration. Check this instance’s registration and update Portal.");
        return (await response.Content.ReadFromJsonAsync<PortalManagedRegistrationCode>(Json,token))?.Code ?? throw new InvalidOperationException("Portal returned no registration code.");
    }

    public async Task<string> ActivateAsync(string code, string channel, IProgress<string>? progress = null, CancellationToken token = default, Func<Task>? beforeRestart = null)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128 || channel is not ("development" or "stable")) throw new ArgumentException("Enter a licence code and select development or stable.");
        if(code.Trim().StartsWith("TRIAL-",StringComparison.OrdinalIgnoreCase) || code.Trim().StartsWith("GRANT-",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That is a licence reference. Open your Portal licence list, generate a licence code if needed, and copy the code beginning LMS-.");
        if (!Uri.TryCreate(PortalUrl,UriKind.Absolute,out var portal) || portal.Scheme != "https" || portal.AbsolutePath!="/" || portal.UserInfo.Length>0 || portal.Query.Length>0)
            throw new InvalidOperationException("Portal activation requires the configured HTTPS Portal origin.");
        await InstallLock.WaitAsync(token);
        try
        {
            var settings = await EnsureSettingsAsync(token);
            if (settings.PortalInstanceId is not null && settings.IsEnabled && !IsInstallationPending(settings)) throw new InvalidOperationException("This installation already has a Portal registration. Remove it before using another licence.");
            var identity = await identities.GetOrCreateIdentityAsync(token);
            settings = (await store.GetAsync(token))!;
            var privateKey = await secrets.ResolveSecretAsync(identity.PrivateKeySecretReference,token) ?? throw new InvalidOperationException("Installation identity could not be loaded.");
            using var key = ECDsa.Create();key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey),out _);
            var installation = new PortalLicenseInstallation(settings.LocalInstanceId,identity.PublicKey,Environment.MachineName,Environment.MachineName,
                LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly),identity.PublicKeyFingerprint,channel,DateTimeOffset.UtcNow,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            var payload=JsonSerializer.Serialize(installation,Json);
            var signature=Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(code.Trim()+"\n"+payload),HashAlgorithmName.SHA256));
            using var http=CreateClient(portal);
            progress?.Report("Validating registration with the Portal…");
            var registrationOnly=code.Trim().StartsWith("PORTAL-",StringComparison.Ordinal);
            using var response=await http.PostAsJsonAsync(registrationOnly?"/api/portal/register":"/api/licenses/activate",new PortalLicenseActivationRequest(code.Trim(),payload,signature),Json,token);
            if(!response.IsSuccessStatusCode) throw new InvalidOperationException("The Portal could not accept this code. Check that it is current and unused. Free registration codes begin PORTAL-.");
            var activation=await response.Content.ReadFromJsonAsync<PortalLicenseActivationResponse>(Json,token) ?? throw new InvalidOperationException("Invalid activation response.");
            settings = await SaveRegistrationAsync(activation.Registration, settings);
            var manifest=VerifyManifest(activation.Addon);
            if(manifest.DownloadPath!="/api/licenses/addon/"+channel)throw new InvalidOperationException("The signed add-on channel does not match your selection.");
            return await InstallAsync(http,manifest,activation.Registration,settings,progress,token,beforeRestart);
        }
        finally { InstallLock.Release(); }
    }

    public async Task<string> UpdateAsync(string channel,IProgress<string>? progress=null,CancellationToken token=default,Func<Task>? beforeRestart=null)
    {
        if(channel is not ("development" or "stable"))throw new ArgumentException("Choose an add-on channel.");
        await InstallLock.WaitAsync(token);
        try
        {
            var settings=await store.GetAsync(token);
            if(settings?.PortalInstanceId is null || !settings.IsEnabled)throw new InvalidOperationException("Register this instance with the Portal first.");
            var secret=await secrets.ResolveSecretAsync(settings.PortalApiSecretReference!,token) ?? throw new InvalidOperationException("Portal credentials are unavailable.");
            if(!Uri.TryCreate(PortalUrl,UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.AbsolutePath!="/"||origin.UserInfo.Length>0||origin.Query.Length>0)
                throw new InvalidOperationException("Portal updates require the configured HTTPS Portal origin.");
            using var http=CreateClient(origin);
            using var request=new HttpRequestMessage(HttpMethod.Get,"/api/licenses/manifest/"+channel);
            request.Headers.Add("X-LMS-Portal-Instance-Id",settings.PortalInstanceId.ToString());
            request.Headers.Add("X-LMS-Portal-Api-Key-Id",settings.PortalApiKeyId);
            request.Headers.Add("X-LMS-Portal-Api-Secret",secret);
            progress?.Report("Checking the Portal add-on…");
            using var response=await http.SendAsync(request,token);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException("The Portal add-on is unavailable. Check the Portal connection and account.");
            var signed=await response.Content.ReadFromJsonAsync<PortalSignedAddonManifest>(Json,token) ?? throw new InvalidOperationException("Invalid add-on response.");
            var manifest=VerifyManifest(signed);
            if(manifest.DownloadPath!="/api/licenses/addon/"+channel)throw new InvalidOperationException("The signed add-on channel does not match your selection.");
            return await InstallAsync(http,manifest,new(settings.PortalInstanceId.Value,settings.PortalOrganizationId!.Value,settings.PortalOrganizationName!,settings.PortalApiKeyId!,secret,"portal",DateTimeOffset.MinValue),settings,progress,token,beforeRestart);
        }
        finally{InstallLock.Release();}
    }
    private HttpClient CreateClient(Uri origin)
    {
        var http = clients?.CreateClient("PortalActivation") ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        http.BaseAddress = origin; http.Timeout = TimeSpan.FromMinutes(3); return http;
    }

    public async Task UnregisterAsync(CancellationToken token = default, Func<Task>? beforeRestart = null)
    {
        await InstallLock.WaitAsync(token);
        try
        {
            var settings = await store.GetAsync(token);
            if (settings?.PortalInstanceId is null) return;
            var secret = await secrets.ResolveSecretAsync(settings.PortalApiSecretReference!, token)
                ?? throw new InvalidOperationException("Portal credentials are unavailable. Check this instance's registration in the Portal.");
            if (!Uri.TryCreate(settings.PortalBaseUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
                origin.AbsolutePath != "/" || origin.UserInfo.Length > 0 || origin.Query.Length > 0)
                throw new InvalidOperationException("Unregister requires the registered HTTPS Portal origin.");
            using var http = CreateClient(origin);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/licenses/unregister");
            request.Headers.Add("X-LMS-Portal-Instance-Id", settings.PortalInstanceId.ToString());
            request.Headers.Add("X-LMS-Portal-Api-Key-Id", settings.PortalApiKeyId);
            request.Headers.Add("X-LMS-Portal-Api-Secret", secret);
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("The Portal could not unregister this instance. Registration is unchanged; please retry.");
            // Keep credentials until the Portal acknowledges release. If local persistence
            // fails, retrying the acknowledged release is safe and never releases two seats.
            await store.SaveAsync(settings with { IsEnabled=false,PortalInstanceId=null,PortalOrganizationId=null,PortalOrganizationName=null,
                PortalApiKeyId=null,PortalApiSecretReference=null,LastConnectedAtUtc=null,LastHeartbeatAtUtc=null,
                LastConnectionStatus="Unregistered. This instance is LMS Community Edition.",UpdatedAtUtc=DateTimeOffset.UtcNow },CancellationToken.None);
            if (!string.IsNullOrEmpty(settings.PortalApiSecretReference))
                await secrets.DeleteSecretAsync(settings.PortalApiSecretReference, CancellationToken.None);
            if (paths is not null)
            {
                var module = Path.Combine(PluginDirectory(configuration, paths.ApplicationDirectory), "LinuxMadeSane.Connect.Client");
                if (Directory.Exists(module))
                {
                    // Renaming only the installed module prevents it loading after restart.
                    // Keep all instance data and installation identity for reactivation.
                    var disabled = module + ".unregistered-" + Guid.NewGuid().ToString("N");
                    Directory.Move(module, disabled);
                    try { Directory.Delete(disabled, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    await RestartAsync(beforeRestart);
                }
            }
        }
        finally { InstallLock.Release(); }
    }
    private async Task<PortalConnectionSettings> SaveRegistrationAsync(PortalLicenseRegistration registration, PortalConnectionSettings settings)
    {
        var reference = await secrets.StoreSecretAsync(registration.ApiSecret, $"portal-registration:{settings.LocalInstanceId:N}", CancellationToken.None);
        var next = settings with { PortalBaseUrl=PortalUrl, IsEnabled=true, PortalOrganizationId=registration.OrganizationId,
            PortalOrganizationName=registration.OrganizationName, PortalInstanceId=registration.InstanceId,
            PortalApiKeyId=registration.ApiKeyId, PortalApiSecretReference=reference,
            LastConnectionStatus="Portal registration accepted. Installing connector…", UpdatedAtUtc=DateTimeOffset.UtcNow };
        try { await store.SaveAsync(next, CancellationToken.None); }
        catch { await secrets.DeleteSecretAsync(reference, CancellationToken.None); throw; }
        if (!string.IsNullOrEmpty(settings.PortalApiSecretReference) && settings.PortalApiSecretReference != reference)
            await secrets.DeleteSecretAsync(settings.PortalApiSecretReference, CancellationToken.None);
        return next;
    }

    private async Task<string> InstallAsync(HttpClient http,PortalAddonManifest manifest,PortalLicenseRegistration registration,
        PortalConnectionSettings settings,IProgress<string>? progress,CancellationToken token,Func<Task>? beforeRestart)
    {
            progress?.Report("Downloading and verifying the Portal add-on…");
            using var request=new HttpRequestMessage(HttpMethod.Get,manifest.DownloadPath);
            request.Headers.Add("X-LMS-Portal-Instance-Id",registration.InstanceId.ToString());
            request.Headers.Add("X-LMS-Portal-Api-Key-Id",registration.ApiKeyId);
            request.Headers.Add("X-LMS-Portal-Api-Secret",registration.ApiSecret);
            using var download=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            download.EnsureSuccessStatusCode();
            var root=PluginDirectory(configuration,paths.ApplicationDirectory);Directory.CreateDirectory(root);
            var scratch=Path.Combine(root,".install-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
            try
            {
                var archive=Path.Combine(scratch,"addon.zip");
                await using(var input=await download.Content.ReadAsStreamAsync(token))
                await using(var output=File.Create(archive))
                {
                    var buffer=new byte[65536];long total=0;int read;
                    while((read=await input.ReadAsync(buffer,token))>0){total+=read;if(total>MaximumArchiveSize || total>manifest.SizeBytes)throw new InvalidOperationException("The add-on exceeds its signed size.");await output.WriteAsync(buffer.AsMemory(0,read),token);}
                    if(total!=manifest.SizeBytes)throw new InvalidOperationException("The add-on download is incomplete.");
                }
                await using(var input=File.OpenRead(archive))
                    if(Convert.ToHexString(await SHA256.HashDataAsync(input,token)).ToLowerInvariant()!=manifest.Sha256)throw new InvalidOperationException("The add-on checksum is invalid.");
                var stage=Path.Combine(scratch,"LinuxMadeSane.Connect.Client");Directory.CreateDirectory(stage);
                ExtractAddon(archive,stage);
                if(!File.Exists(Path.Combine(stage,"LinuxMadeSane.Connect.Client.dll")))throw new InvalidOperationException("The Portal connector is missing.");
                await File.WriteAllTextAsync(Path.Combine(stage,"activation.json"),JsonSerializer.Serialize(new { product=registration.Product, expiresAtUtc=registration.ExpiresAtUtc==DateTimeOffset.MinValue ? (DateTimeOffset?)null : registration.ExpiresAtUtc, manifest.Version },Json),token);
                var current=Path.Combine(root,"LinuxMadeSane.Connect.Client");var previous=Path.Combine(root,".previous-"+Guid.NewGuid().ToString("N"));
                var reference=settings.PortalApiSecretReference;
                var hadPrevious=Directory.Exists(current);var moved=false;
                try
                {
                    if(hadPrevious)Directory.Move(current,previous);
                    Directory.Move(stage,current);moved=true;
                    await store.SaveAsync(settings with { PortalBaseUrl=PortalUrl,IsEnabled=true,PortalOrganizationId=registration.OrganizationId,
                        PortalOrganizationName=registration.OrganizationName,PortalInstanceId=registration.InstanceId,
                        PortalApiKeyId=registration.ApiKeyId,PortalApiSecretReference=reference,
                        LastConnectionStatus="Portal add-on installed. Restart LMS to connect.",UpdatedAtUtc=DateTimeOffset.UtcNow },CancellationToken.None);
                }
                catch
                {
                    if(moved)Directory.Move(current,stage);
                    if(hadPrevious && Directory.Exists(previous))Directory.Move(previous,current);
                    throw;
                }
                // Remove only the module backup created by this operation, after credentials
                // and module installation both succeeded. Never touch application data.
                try { if(hadPrevious)Directory.Delete(previous,true); }catch(IOException){}catch(UnauthorizedAccessException){}
                if(!string.IsNullOrEmpty(settings.PortalApiSecretReference)&&settings.PortalApiSecretReference!=reference)
                    try { await secrets.DeleteSecretAsync(settings.PortalApiSecretReference,CancellationToken.None); }catch(IOException){}
                progress?.Report("Portal registration accepted. Restarting LMS to connect automatically…");
                await RestartAsync(beforeRestart);
                return "Portal registration accepted. LMS is restarting and will connect automatically.";
            }
            finally { if(Directory.Exists(scratch))Directory.Delete(scratch,true); }
    }

    public async Task RestartAsync(Func<Task>? beforeRestart = null)
    {
        var unit=configuration["PortalAddons:ServiceUnit"] ?? "linux-made-sane.service";
        if(!OperatingSystem.IsLinux() || !Directory.Exists("/run/systemd/system") || !System.Text.RegularExpressions.Regex.IsMatch(unit,@"^[a-zA-Z0-9_.@-]+\.service$"))
            throw new InvalidOperationException("Restart this LMS application to load the installed add-on.");
        var info=new System.Diagnostics.ProcessStartInfo("/usr/bin/sudo") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(var argument in new[]{"-n","/usr/bin/systemctl","--no-block","restart",unit})info.ArgumentList.Add(argument);
        if (beforeRestart is not null) await beforeRestart();
        using var process=System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("LMS could not start its service restart.");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();await Task.WhenAll(output,error);
        if(process.ExitCode!=0)throw new InvalidOperationException("The add-on is installed. Restart LMS using its service controls to finish connecting.");
    }

    public static PortalAddonManifest VerifyManifest(PortalSignedAddonManifest signed)
    {
        if(signed is null || string.IsNullOrEmpty(signed.Payload) || signed.Payload.Length>16384 || string.IsNullOrEmpty(signed.Signature)||signed.Signature.Length>1024)
            throw new InvalidOperationException("Invalid signed add-on manifest.");
        using var stream=typeof(PortalAddonBootstrapService).Assembly.GetManifestResourceStream("Lms.PortalAddon.PublicKey")!;
        using var reader=new StreamReader(stream);using var key=ECDsa.Create();key.ImportFromPem(reader.ReadToEnd());
        if(!key.VerifyData(Encoding.UTF8.GetBytes(signed.Payload),Convert.FromBase64String(signed.Signature),HashAlgorithmName.SHA256))throw new InvalidOperationException("The add-on signature is invalid.");
        var manifest=JsonSerializer.Deserialize<PortalAddonManifest>(signed.Payload,Json) ?? throw new InvalidOperationException("Invalid add-on manifest.");
        if(manifest.PluginApiVersion!=1 || string.CompareOrdinal(manifest.MinimumHostVersion,LinuxMadeSaneBuildVersion.GetCurrent(typeof(Program).Assembly))>0 ||
            manifest.SizeBytes<=0 || manifest.SizeBytes>MaximumArchiveSize || manifest.FileName!="portal-addon.zip" ||
            manifest.DownloadPath is not ("/api/licenses/addon/development" or "/api/licenses/addon/stable") ||
            (manifest.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256,"^[a-f0-9]{64}$")))throw new InvalidOperationException("This add-on is not compatible with the installed CE version.");
        return manifest;
    }
    public static void ExtractAddon(string path,string target)
    {
        using var zip=ZipFile.OpenRead(path);long total=0;
        if(zip.Entries.Count>1500)throw new InvalidOperationException("The add-on contains too many files.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in zip.Entries)
        {
            if(entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Any(x=>x is "." or "..") ||
                (entry.ExternalAttributes>>16&0xf000)==0xa000 || !names.Add(entry.FullName))throw new InvalidOperationException("Invalid add-on archive path.");
            var destination=Path.GetFullPath(Path.Combine(target,entry.FullName));
            if(!destination.StartsWith(Path.GetFullPath(target)+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new InvalidOperationException("Invalid add-on archive destination.");
            total+=entry.Length;if(total>512L*1024*1024)throw new InvalidOperationException("The expanded add-on is too large.");
            if(entry.FullName.EndsWith('/')){Directory.CreateDirectory(destination);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);entry.ExtractToFile(destination,false);
        }
    }
}
