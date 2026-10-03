// Copyright (c) Linux Made Sane.
// Licensed under the Business Source License 1.1. See LICENSE for details.
namespace LinuxMadeSane.Application.Contracts;

public sealed class SavedCredentialKeySetupException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
