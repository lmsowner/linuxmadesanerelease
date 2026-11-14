// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web.Services;

public static class FilePaneTransferPolicy
{
    public static void Validate(FileActionPasteRequest request)
    {
        if (request.Items.Count == 0) throw new InvalidOperationException("Select files or folders in the source pane first.");
        if (request.SourceHost.Id != request.DestinationHost.Id) return;
        static string Normalize(string path) => path.TrimEnd('/') is { Length: > 0 } value ? value : "/";
        var destination = Normalize(request.DestinationDirectoryPath);
        foreach (var item in request.Items)
        {
            var source = Normalize(item.SourcePath);
            var target = (destination == "/" ? "" : destination) + "/" + item.DisplayName;
            if (source.Equals(target, StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a different destination folder; these files are already in that folder.");
            if (item.IsDirectory && (destination.Equals(source, StringComparison.Ordinal) || destination.StartsWith(source == "/" ? "/" : source + "/", StringComparison.Ordinal)))
                throw new InvalidOperationException("A folder cannot be copied or moved inside itself. Choose a different destination folder.");
        }
    }
}
