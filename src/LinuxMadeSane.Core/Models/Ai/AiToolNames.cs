// Copyright (c) Linux Made Sane.
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
    public const string InspectHomeLab = "inspect_home_lab";
    public const string InspectHomeLabApplicationConfig = "inspect_home_lab_app_config";
    public const string RepairHomeLabApplicationConfig = "repair_home_lab_app_config";
    public const string RepairHomeLabInstallation = "repair_home_lab_installation";
    public const string ApplyHomeLabPromptRecipe = "apply_home_lab_prompt_recipe";
    public const string RollbackSafeChange = "rollback_safe_change";
    public const string DesktopSetKeyboardLayout = "desktop_set_keyboard_layout";
    public const string DesktopInstallAptPackages = "desktop_install_apt_packages";
}
