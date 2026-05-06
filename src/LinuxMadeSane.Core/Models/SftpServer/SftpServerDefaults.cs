namespace LinuxMadeSane.Core.Models.SftpServer;

public static class SftpServerDefaults
{
    public const string ManagedUsersGroup = "lms-sftp-users";
    public const string PasswordOnlyGroup = "lms-sftp-password";
    public const string PublicKeyOnlyGroup = "lms-sftp-key";
    public const string PublicKeyAndPasswordGroup = "lms-sftp-key-password";
    public const string BasePath = "/srv/lms-sftp";
    public const string ManagedRootDirectory = "/etc/ssh/linuxmadesane/sftp";
    public const string ManagedAuthorizedKeysDirectory = "/etc/ssh/linuxmadesane/sftp/authorized_keys";
    public const string ManagedDropInDirectory = "/etc/ssh/sshd_config.d";
    public const string ManagedDropInPath = "/etc/ssh/sshd_config.d/90-lms-sftp.conf";
    public const string MainSshdConfigPath = "/etc/ssh/sshd_config";
    public const string ManagedConfigStartMarker = "# >>> LMS SFTP managed block >>>";
    public const string ManagedConfigEndMarker = "# <<< LMS SFTP managed block <<<";
    public const string BackupDirectory = "data/sftp-backups";
    public const string NoLoginShell = "/usr/sbin/nologin";

    public static IReadOnlyList<string> ManagedGroups =>
    [
        ManagedUsersGroup,
        PasswordOnlyGroup,
        PublicKeyOnlyGroup,
        PublicKeyAndPasswordGroup
    ];
}
