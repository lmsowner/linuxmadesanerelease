// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using LinuxMadeSane.Core.Enums;

namespace LinuxMadeSane.Core.Models.Ai;

public sealed record AiApprovalActor(
    string ActorName,
    AiUserTrustLevel TrustLevel,
    bool AdminOverrideExists);
