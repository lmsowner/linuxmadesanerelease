// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Core.Models;

public sealed record FolderDuplicateFile(string Name, long SizeBytes, string Identity);
public sealed record FolderDuplicateGroup(string Sha256, FolderDuplicateFile Keep, IReadOnlyList<FolderDuplicateFile> Duplicates);
public sealed record FolderDuplicateMove(string SourceName, string DestinationName);
public sealed record FolderDuplicateResult(string RootPath, string RootIdentity, int FilesScanned, int FilesHashed,
    IReadOnlyList<FolderDuplicateGroup> Groups, IReadOnlyList<FolderDuplicateMove> MovedFiles, IReadOnlyList<string> Warnings);
