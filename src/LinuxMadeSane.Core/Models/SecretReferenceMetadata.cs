// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

namespace LinuxMadeSane.Core.Models;

public sealed record SecretReferenceMetadata(
    string Reference,
    string Purpose,
    bool RequiresProtectedStorage);
