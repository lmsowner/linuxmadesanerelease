// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models;

public sealed record SshCredentialType(
    AuthenticationType AuthenticationType,
    string DisplayName,
    string Description,
    bool RequiresSecretReference);
