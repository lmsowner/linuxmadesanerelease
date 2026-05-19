// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Core.Models.Ai;

public static class AiToolNames
{
    public const string ListServers = "list_servers";
    public const string GetServerSummary = "get_server_summary";
    public const string GetServerHealth = "get_server_health";
    public const string ListServices = "list_services";
    public const string RestartService = "restart_service";
    public const string BrowseDirectory = "browse_directory";
    public const string ReadFile = "read_file";
    public const string RunCommand = "run_command";
    public const string WriteFileWithConfirmation = "write_file_with_confirmation";
    public const string InstallPackageWithConfirmation = "install_package_with_confirmation";
    public const string RollbackSafeChange = "rollback_safe_change";
}
