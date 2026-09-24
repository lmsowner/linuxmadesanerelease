// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinuxMadeSane.Core.Enums;
using LinuxMadeSane.Core.Models;

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed class SecurityUserEditor
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string LinuxUsername { get; set; } = string.Empty;

    [Range(SecuritySessionPolicy.MinimumSessionLifetimeMinutes, SecuritySessionPolicy.MaximumSessionLifetimeMinutes)]
    public int SessionLifetimeMinutes { get; set; } = SecuritySessionPolicy.DefaultSessionLifetimeMinutes;

    public RemoteAccessSshAuthenticationMode SshAuthenticationMode { get; set; } = RemoteAccessSshAuthenticationMode.Password;

    public string AuthorizedKeyEntries { get; set; } = string.Empty;
}
