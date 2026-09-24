// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class SecurityUserAccessEditor
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;

    [Required]
    public string LinuxUsername { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    [Range(SecuritySessionPolicy.MinimumSessionLifetimeMinutes, SecuritySessionPolicy.MaximumSessionLifetimeMinutes)]
    public int SessionLifetimeMinutes { get; set; } = SecuritySessionPolicy.DefaultSessionLifetimeMinutes;

    public RemoteAccessSshAuthenticationMode SshAuthenticationMode { get; set; } = RemoteAccessSshAuthenticationMode.Password;
    public string AuthorizedKeyEntries { get; set; } = string.Empty;
}
