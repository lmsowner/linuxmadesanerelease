// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

internal static class SftpApiReference
{
    internal const string ExampleKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJi1lCnyfJgL4JxOS2FixS4fUuZ2/tGIXF5llKpKk81P example-only";
    internal const string Connection = """
        Connect directly to the HTTPS listener on the LMS address selected for the credential. Each request requires Authorization: Bearer <token>. The actual TCP peer must match one of the configured source IPs or CIDR networks. A DNS name can be used if it resolves to the selected local address and matches the TLS certificate. Forwarded headers cannot select an interface or impersonate a permitted peer. Customer uploads continue to use SSH/SFTP.
        """;
    internal const string Setup = "Replace sftp.example.net:5443 with the hostname and port for your credential’s LMS interface. YOUR_API_TOKEN stands for the credential from your secret store. Each curl example is one command. In Windows PowerShell, use curl.exe.";
    internal const string Accounts = "A credential owns accounts it creates. Rotating or revoking and reissuing its token preserves that ownership. Existing administrator-created accounts and accounts belonging to other credentials are outside its scope. Usernames are lowercase Linux names, up to 32 characters. Configure managed SFTP on the Setup tab before provisioning.";
    internal const string Modes = "Authentication modes are PasswordOnly, PublicKeyOnly and PasswordAndPublicKey. PasswordAndPublicKey requires both factors. Key-based modes require a valid single-line OpenSSH public key. PublicKeyOnly disables password login. Supply a new password when switching back to a password-capable mode. Passwords are accepted only in JSON request bodies.";
    internal const string Files = "Paths are relative to the user's /files folder. URL-encode query values. Parent traversal, absolute paths and symbolic links are refused. Transfers support regular files up to 50 MiB and listings up to 10,000 entries. File operations use the managed account’s Linux permissions. Disable blocks customer SSH login. The application's scoped credential can still collect remaining files. Revoke it to block API requests.";
    internal const string Import = "For an import, list the folder, download a regular file and commit it to the receiving application. Delete the source only after that commit succeeds and only when cleanup is enabled. Use unique incoming filenames and deduplicate imports before retrying after a timeout.";
    internal const string Errors = "200 returns JSON unless a download is specified. 201 means created and 204 has no body. 400 indicates invalid input, 401 an invalid or revoked credential, 403 a TLS, network or file-access restriction, 404 an unavailable resource, 409 a conflict, 413 a size limit, 415 the wrong upload format, 429 busy transfers and 503 a file helper failure. After an uncertain create, check existence before retrying. Host bootstrap, repair and service restarts are administrator actions in LMS.";
    internal sealed record Parameter(string Name, string Location, string Type, string Requirement, string Details);
    private const string NameRule = "1–32 characters. Start with a lowercase ASCII letter or underscore. Continue with lowercase letters, digits, underscores or hyphens.";
    private const string PasswordRule = "1–1,024 characters. No control characters or leading or trailing whitespace.";
    private const string KeyRule = "One OpenSSH public key, up to 16,384 characters on one line. Supply the key type, base64 key and optional comment. Supported types are ssh-ed25519, ssh-rsa, ecdsa-sha2-nistp256, ecdsa-sha2-nistp384, ecdsa-sha2-nistp521, sk-ssh-ed25519@openssh.com and sk-ecdsa-sha2-nistp256@openssh.com.";
    private const string PathRule = "Relative to /files. URL-encode the query value. Maximum 4,096 characters and 255 per component. Use forward slashes. Empty components, dot segments, backslashes, control characters and symbolic links are refused.";
    private static readonly Parameter AccountName = new("username", "URL path", "string", "Required", NameRule);
    private static readonly Parameter NewName = new("username", "JSON", "string", "Required", NameRule + " Choose an unused account name.");
    private static readonly Parameter Mode = new("authenticationMode", "JSON", "string enum", "Required", "PasswordOnly, PublicKeyOnly or PasswordAndPublicKey. Names are case-insensitive. Numeric values are rejected.");
    private static readonly Parameter CreatePassword = new("password", "JSON", "string", "Conditional", "Required for PasswordOnly and PasswordAndPublicKey. Omit for PublicKeyOnly. " + PasswordRule);
    private static readonly Parameter ChangePassword = new("password", "JSON", "string", "Conditional", "Required when enabling password login from PublicKeyOnly. Omit to keep an existing usable password. " + PasswordRule);
    private static readonly Parameter ResetPassword = new("password", "JSON", "string", "Required", PasswordRule);
    private static readonly Parameter CreateKey = new("publicKey", "JSON", "string", "Conditional", "Required for PublicKeyOnly and PasswordAndPublicKey. Optional for PasswordOnly. " + KeyRule);
    private static readonly Parameter ReplaceKey = new("publicKey", "JSON", "string", "Required", KeyRule);
    private static readonly Parameter GuideHost = new("host", "Query", "string", "Optional", "SSH hostname or IP address. Defaults to the API request hostname. DNS names are at most 253 characters. Supply the hostname without a scheme or port.");
    private static readonly Parameter GuideKey = new("privateKeyPath", "Query", "string", "Optional", "Path printed in the example command. Defaults to ~/.ssh/id_ed25519. Up to 256 characters matching [~/a-zA-Z0-9_][a-zA-Z0-9_./-]*. The client keeps its private key.");
    private static readonly Parameter Folder = new("path", "Query", "string", "Optional", "Defaults to the upload root. " + PathRule);
    private static readonly Parameter Destination = new("path", "Query", "string", "Optional", "Defaults to the uploaded filename in the root. The parent folder must exist. " + PathRule);
    private static readonly Parameter ExistingFile = new("path", "Query", "string", "Required", "Identify one existing regular file. " + PathRule);
    private static readonly Parameter UploadFile = new("file", "Multipart form", "binary file", "Required", "One file part named file, up to 50 MiB. The entire multipart request may be up to 51 MiB.");
    internal sealed record Entry(string Method, string RouteSuffix, string Purpose, string Description, string Response,
        string Query = "", string? Json = null, string? Upload = null, string? Download = null, Parameter[]? Inputs = null)
    {
        internal IEnumerable<Parameter> Parameters => RouteSuffix.Contains("{username}")
            ? (Inputs ?? []).Prepend(AccountName) : Inputs ?? [];
        internal string Path => SftpServerApi.Prefix + RouteSuffix;
        internal string ExampleUrl => "https://sftp.example.net:5443" + Path.Replace("{username}", "import_user") + Query;
    }
    internal static readonly Entry[] Entries =
    [
        new("GET", "/", "Test the API connection",
            "Call this when connecting an application for the first time or after rotating its token. A successful response confirms that the HTTPS request reached the SFTP API and passed its token and network checks. It returns the API version.", """200 {"service":"LMS SFTP API","version":1}"""),
        new("GET", "/server/status", "Check SFTP readiness",
            "Check this before onboarding a customer, or when the API responds but customers cannot connect over SFTP. The three flags tell you whether LMS manages SFTP, the SSH service is active and the host configuration passes validation.", """200 {"managed":true,"sshActive":true,"configurationValid":true}"""),
        new("POST", "/users", "Provision an upload account",
            "Call this when a customer needs their own SFTP login and upload folder. Supply a unique username, an authentication mode and the password, public key or both required by that mode. The Location header identifies the new account. If the request times out, check whether your credential owns the account before retrying.", """201 {"username":"import_user"} plus Location""", Json: """{"username":"import_user","authenticationMode":"PublicKeyOnly","publicKey":"<your OpenSSH public key>"}""", Inputs: [NewName, Mode, CreatePassword, CreateKey]),
        new("GET", "/users", "List your application’s accounts",
            "Use this to populate an account picker or reconcile the accounts your application provisioned. Each entry includes the username, login mode, key presence and enabled state. An empty list means this credential owns no managed accounts.", """200 [{"username":"import_user","authenticationMode":"PublicKeyOnly","hasPublicKey":true,"enabled":true}]"""),
        new("GET", "/users/{username}", "Inspect one account",
            "Look up a customer before changing their login settings or troubleshooting access. Check enabled to see whether login is allowed and hasPublicKey to see whether a key is registered. A 404 means the account is unavailable to this credential.", """200 {"username":"import_user","authenticationMode":"PublicKeyOnly","hasPublicKey":true,"enabled":true}"""),
        new("GET", "/users/{username}/exists", "Check account ownership",
            "Use this after an interrupted provisioning request to see whether your application already owns the account. A false result can also mean another owner has the name, so account creation can still return a conflict.", """200 {"exists":true}"""),
        new("PUT", "/users/{username}/public-key", "Register or rotate an SSH key",
            "Use this when a customer first supplies a key or replaces a device. Send their single-line OpenSSH public key. It replaces all keys registered for this account. For a password-only account, change the authentication mode separately when you are ready to require the key.", "204", Json: """{"publicKey":"<your OpenSSH public key>"}""", Inputs: [ReplaceKey]),
        new("PUT", "/users/{username}/auth", "Change login requirements",
            "Use this to move a customer from password login to an SSH key, or to require both factors. Register the public key first for either key-based mode. Switching to PublicKeyOnly disables password login. Supply a new password when moving back from that mode.", "204", Json: """{"authenticationMode":"PasswordAndPublicKey","password":"<new password>"}""", Inputs: [Mode, ChangePassword]),
        new("DELETE", "/users/{username}/public-key", "Remove registered SSH keys",
            "Use this to retire a customer’s keys while keeping password access. Switch the account to PasswordOnly first. LMS returns 409 if the current mode still requires a key.", "204 or 409 if the account requires a key"),
        new("GET", "/users/{username}/public-key-example", "Show a customer how to generate a key",
            "Use the returned key-generation command and sample format in customer onboarding instructions. The customer generates a key pair on their device, keeps the private key there and sends you the public key to register.", "200 JSON with generate and publicKeyExample"),
        new("GET", "/users/{username}/auth-guide", "Get connection instructions",
            "Use this in a customer welcome screen or support reply. It returns the account’s current login mode, an SFTP command and its upload directory. Supply host and privateKeyPath to tailor the displayed command to the customer’s SSH hostname and local key location.", """200 {"authenticationMode":"PublicKeyOnly","command":"sftp -i ~/.ssh/import import_user@sftp.example.net","uploadDirectory":"/files"}""", Query: "?host=sftp.example.net&privateKeyPath=~/.ssh/import", Inputs: [GuideHost, GuideKey]),
        new("PUT", "/users/{username}/password", "Reset an SFTP password",
            "Use this when a customer forgets their password or you need to rotate it. Send the new password in the JSON body for an account that allows password authentication. Update the customer’s SFTP client before its next login.", "204", Json: """{"password":"<new password>"}""", Inputs: [ResetPassword]),
        new("PUT", "/users/{username}/disable", "Pause customer logins",
            "Use this to suspend new customer logins while keeping their account, credentials and uploaded files. Your application can still collect outstanding files with its API token.", "204"),
        new("PUT", "/users/{username}/enable", "Resume customer logins",
            "Use this after a temporary suspension. The customer can log in again with the account’s configured authentication mode and credentials, using the same upload folder.", "204"),
        new("GET", "/users/{username}/files", "Find files waiting for import",
            "Poll this endpoint to discover incoming files. Omit path to list the account’s upload root, or supply a relative folder such as incoming. Check isDirectory before downloading an entry. File sizes are in bytes.", """200 [{"name":"orders.csv","isDirectory":false,"size":734}]""", Query: "?path=incoming", Inputs: [Folder]),
        new("GET", "/users/{username}/files/content", "Download a file for import",
            "After listing a folder, pass the file’s relative path to read its exact bytes. Save or process the response as binary data. The source stays in the upload folder so you can retry a failed import.", "200 application/octet-stream", Query: "?path=incoming/orders.csv", Download: "orders.csv", Inputs: [ExistingFile]),
        new("POST", "/users/{username}/files", "Upload a new file",
            "Use this to deliver a new file into the customer’s SFTP area. Send multipart field file. The optional path selects the destination in an existing folder. An existing file returns 409, which protects it from an accidental overwrite.", """201 {"path":"incoming/orders.csv"} or 409 if already present""", Query: "?path=incoming/orders.csv", Upload: "orders.csv", Inputs: [Destination, UploadFile]),
        new("PUT", "/users/{username}/files/content", "Replace an existing file",
            "Use this to correct a file already present in the customer’s folder. Send the replacement in multipart field file and identify the existing file with the path query parameter. The next download reads the new contents.", "204", Query: "?path=incoming/orders.csv", Upload: "corrected.csv", Inputs: [ExistingFile, UploadFile]),
        new("DELETE", "/users/{username}/files", "Delete an imported file",
            "Use this for optional cleanup after your application has successfully committed an import. Pass the same relative path you downloaded. This removes the source file, so retain it when processing fails or needs a retry.", "204", Query: "?path=incoming/orders.csv", Inputs: [ExistingFile]),
        new("DELETE", "/users/{username}", "Permanently remove an account",
            "Use this when offboarding a customer and their uploads are no longer needed. Collect any outstanding files first. Deletion removes the managed Linux account, its keys and its upload folder with all its contents. Use disable for a temporary suspension.", "204")
    ];
}
