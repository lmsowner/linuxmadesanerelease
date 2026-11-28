// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Data.Common;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using LinuxMadeSane.Application.Services;
using LinuxMadeSane.Core.Models;
using Microsoft.AspNetCore.DataProtection;

namespace LinuxMadeSane.AccountEnrollment;

public sealed record EnrollmentRequest(Guid OperationId, string Email, int SessionMinutes, string Issuer, string? Ticket, string? Code, bool CheckOnly = false);
public sealed record EnrollmentReply(string Status, string? Ticket = null, string? OtpUri = null);
public static class AccountEnrollmentWorker
{
    private sealed record Challenge(Guid OperationId, string Email, int SessionMinutes, string Secret, DateTimeOffset Expires);

    public static async Task<int> Main()
    {
        try
        {
            var request = JsonSerializer.Deserialize<EnrollmentRequest>(await Console.In.ReadToEndAsync());
            if (request is null || request.OperationId == Guid.Empty || !MailAddress.TryCreate(request.Email, out var email) ||
                email.Address != request.Email || request.Email.Length > 254 || string.IsNullOrWhiteSpace(request.Issuer) || request.Issuer.Length > 300)
                return 1;
            // Bind the SQLite provider shipped with this destination, including older CE servicing releases.
            var dbType = Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite", throwOnError: true)!;
            await using var db = (DbConnection)Activator.CreateInstance(dbType)!;
            var connection = new DbConnectionStringBuilder { ConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__LinuxMadeSane") };
            connection["Mode"] = request.CheckOnly ? "ReadOnly" : "ReadWrite";
            db.ConnectionString = connection.ConnectionString;
            await db.OpenAsync();
            if (request.CheckOnly)
            {
                var existing = await FindExistingAsync(db, null, request.Email, null);
                Console.Write(JsonSerializer.Serialize(existing ?? new EnrollmentReply("missing")));
                return 0;
            }

            var keys = new DirectoryInfo(Environment.GetEnvironmentVariable("DataProtection__KeyDirectory") ?? "");
            if (!keys.Exists) return 1;
            var protection = DataProtectionProvider.Create(keys, options => options.SetApplicationName("LinuxMadeSane"));
            Console.Write(JsonSerializer.Serialize(await ExecuteAsync(request, db, protection)));
            return 0;
        }
        catch (Exception)
        {
            // SSH diagnostics must never include the submitted code, ticket or OTP seed.
            Console.Error.WriteLine("Could not access the destination LMS account store.");
            return 1;
        }
    }

    public static async Task<EnrollmentReply> ExecuteAsync(EnrollmentRequest request, DbConnection db, IDataProtectionProvider protection)
    {
        var tickets = protection.CreateProtector("LinuxMadeSane.AccountCopy.v1");
        await using var transaction = await db.BeginTransactionAsync();
        var existing = await FindExistingAsync(db, transaction, request.Email, request.OperationId);
        if (existing is not null) return existing;

        if (request.Ticket is null)
        {
            var secret = TotpAuthenticator.GenerateSecret();
            var challenge = new Challenge(request.OperationId, request.Email, request.SessionMinutes, secret, DateTimeOffset.UtcNow.AddMinutes(10));
            return new("pending", tickets.Protect(JsonSerializer.Serialize(challenge)), TotpAuthenticator.BuildOtpUri(request.Email, secret, request.Issuer));
        }

        Challenge? pending;
        try { pending = JsonSerializer.Deserialize<Challenge>(tickets.Unprotect(request.Ticket)); }
        catch (CryptographicException) { return new("expired"); }
        if (pending is null || pending.OperationId != request.OperationId || pending.Email != request.Email || pending.Expires <= DateTimeOffset.UtcNow)
            return new("expired");
        if (request.Code is not { Length: 6 } pin || !pin.All(char.IsAsciiDigit) || !TotpAuthenticator.ValidateCode(pending.Secret, pin))
            return new("invalid");

        // Write the secret and login together using the schema shared by stable and development.
        // Empty LinuxUsername and the default unmanaged SSH fields make this an LMS web account.
        await using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO protected_secrets (Reference,Purpose,ProtectedValue,CreatedAtUtc,UpdatedAtUtc)
            VALUES (@reference,@purpose,@protected,@now,@now);
            INSERT INTO security_users
              (Id, Email, NormalizedEmail, LinuxUsername, IsEnabled, SessionLifetimeMinutes, SshAuthenticationMode,
               AuthorizedKeyEntries, IsLocalAccountManaged, OtpSecretReference, CreatedAtUtc, UpdatedAtUtc, DomainOtpSecretReferencesJson)
            VALUES (@id,@email,@normalized,'',1,@minutes,0,'',0,@reference,@now,@now,'{}');
            """;
        Add(insert, "@reference", $"secret://{Guid.NewGuid():N}");
        Add(insert, "@purpose", $"security-user-otp:{pending.Email}");
        Add(insert, "@protected", protection.CreateProtector(ProtectedSecretPurpose.Value).Protect(pending.Secret));
        Add(insert, "@now", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
        Add(insert, "@id", pending.OperationId.ToString().ToUpperInvariant());
        Add(insert, "@email", pending.Email);
        Add(insert, "@normalized", pending.Email.Trim().ToUpperInvariant());
        Add(insert, "@minutes", SecuritySessionPolicy.NormalizeSessionLifetimeMinutes(pending.SessionMinutes));
        await insert.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new("verified");
    }

    private static async Task<EnrollmentReply?> FindExistingAsync(DbConnection db, DbTransaction? transaction, string email, Guid? operationId)
    {
        await using var lookup = db.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT Id,IsEnabled FROM security_users WHERE NormalizedEmail=@email";
        Add(lookup, "@email", email.Trim().ToUpperInvariant());
        await using var reader = await lookup.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var alreadyVerified = operationId.HasValue && Guid.TryParse(reader.GetString(0), out var id)
            && id == operationId && reader.GetBoolean(1);
        return new(alreadyVerified ? "verified" : "exists");
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
