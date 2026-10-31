// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Application.Contracts;

public sealed class SavedCredentialKeySetupException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
