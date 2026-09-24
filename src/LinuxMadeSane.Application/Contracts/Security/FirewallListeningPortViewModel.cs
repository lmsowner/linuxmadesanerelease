// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

namespace LinuxMadeSane.Application.Contracts.Security;

public sealed record FirewallListeningPortViewModel(
    string Protocol,
    int Port,
    string Destination);
