// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
using System.Buffers.Binary;
using NetVips;
using TiffLibrary;

namespace LinuxMadeSane.Web.Services;

/// <summary>A seekable view of one TIFF page. Pixel data stays on the host and is read in bounded ranges.</summary>
public sealed class TiffDocumentThumbnailSession(
    long length, Func<CancellationToken, Task<Stream>> openStream)
{
    private static readonly SemaphoreSlim BackgroundCounts = new(1, 1);
    private static readonly Lazy<bool> NativeReady = new(() =>
    {
        if (!ModuleInitializer.VipsInitialized) throw new InvalidOperationException("The TIFF rendering library could not start.");
        NetVips.NetVips.Concurrency = 1;
        Cache.Max = 0;
        return true;
    });
    private readonly SemaphoreSlim indexGate = new(1, 1);
    private readonly List<Page> pages = [];
    private readonly HashSet<long> seen = [];
    private bool initialized, bigTiff, littleEndian;
    private long next;
    private sealed record Page(long Offset, long NextPointer);

    private async Task<Page?> AdvanceAsync(TiffFileReader reader, CancellationToken token)
    {
        if (seen.Contains(next)) throw new InvalidDataException("The TIFF page directory contains a cycle.");
        var offset = next;
        var directory = await reader.ReadImageFileDirectoryAsync(new TiffStreamOffset(offset), token);
        var reduced = false;
        if (directory.Contains(TiffTag.NewSubfileType))
        {
            using var fields = reader.CreateFieldReader();
            var type = await fields.ReadLongFieldAsync(directory.FindEntry(TiffTag.NewSubfileType), sizeLimit: 1, cancellationToken: token);
            reduced = type.Count > 0 && (type[0] & 1) != 0;
        }
        // Commit the index only after all cancellable reads succeed.
        seen.Add(offset); next = directory.NextOffset.ToInt64();
        if (reduced) return null;
        var page = new Page(offset, checked(offset + (bigTiff ? 8 : 2) + directory.Count * (bigTiff ? 20L : 12L)));
        pages.Add(page); return page;
    }
    private void Initialize(TiffFileReader reader)
    {
        if (initialized) return;
        bigTiff = reader.IsBigTiff; littleEndian = reader.IsLittleEndian;
        next = reader.FirstImageFileDirectoryOffset.ToInt64(); initialized = true;
    }
    public async Task<FileThumbnailCacheEntry> RenderAsync(int pageIndex, int maxDimension, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        await using var input = await openStream(token);
        if (input.Length != length) throw new IOException("The TIFF file changed. Refresh the folder.");
        Page page; int knownCount; bool complete;
        await indexGate.WaitAsync(token);
        try
        {
            using var reader = await TiffFileReader.OpenAsync(input, leaveOpen: true, cancellationToken: token);
            Initialize(reader);
            while (pages.Count <= pageIndex && next != 0) await AdvanceAsync(reader, token);
            if (pageIndex >= pages.Count) throw new InvalidDataException("This TIFF page is unavailable.");
            page = pages[pageIndex]; complete = next == 0; knownCount = pages.Count + (complete ? 0 : 1);
        }
        finally { indexGate.Release(); }
        // Native decoding runs off the Blazor circuit, and produces only the small output image.
        var jpeg = await Task.Run(() =>
        {
            _ = NativeReady.Value;
            using var stream = new PageStream(input, token);
            stream.AddPatch(bigTiff ? 8 : 4, OffsetBytes(page.Offset));
            stream.AddPatch(page.NextPointer, new byte[bigTiff ? 8 : 4]);
            using var source = new SourceCustom();
            Exception? sourceError = null;
            source.OnRead += (buffer, count) => { try { return stream.Read(buffer, 0, count); } catch (Exception ex) { sourceError = ex; return -1; } };
            source.OnSeek += (offset, origin) => { try { return stream.Seek(offset, origin); } catch (Exception ex) { sourceError = ex; return -1; } };
            try
            {
                // The virtual page has no following IFD. libtiff cannot scan other pages before rendering.
                using var thumbnail = Image.ThumbnailSource(source, maxDimension, height: maxDimension,
                    optionString: "page=0,n=1,unlimited=true", size: Enums.Size.Down);
                using var cancel = token.Register(() => thumbnail.SetKill(true));
                token.ThrowIfCancellationRequested();
                var result = thumbnail.JpegsaveBuffer(q: 85, keep: Enums.ForeignKeep.None);
                token.ThrowIfCancellationRequested();
                if (sourceError is not null) throw new IOException("Could not read the TIFF page.", sourceError);
                return result;
            }
            catch (VipsException) { token.ThrowIfCancellationRequested(); if (sourceError is not null) throw new IOException("Could not read the TIFF page.", sourceError); throw; }
        }, token);
        return new(jpeg, "image/jpeg", knownCount, pageIndex, complete);
    }
    private byte[] OffsetBytes(long offset)
    {
        var bytes = new byte[bigTiff ? 8 : 4];
        if (bigTiff)
        {
            if (littleEndian) BinaryPrimitives.WriteInt64LittleEndian(bytes, offset); else BinaryPrimitives.WriteInt64BigEndian(bytes, offset);
        }
        else if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)offset));
        else BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)offset));
        return bytes;
    }
    public Task<int> CountPagesAsync(Func<bool> foregroundPending, CancellationToken token = default)=>
        Task.Run(()=>CountPagesCoreAsync(foregroundPending,token),token);

    private async Task<int> CountPagesCoreAsync(Func<bool> foregroundPending,CancellationToken token)
    {
        await BackgroundCounts.WaitAsync(token);
        try
        {
            while (foregroundPending()) await Task.Delay(50, token);
            await using var stream = await openStream(token);
            using var reader = await TiffFileReader.OpenAsync(stream, leaveOpen: true, cancellationToken: token);
            while (true)
            {
                await Task.Delay(50, token);
                if (foregroundPending()) continue;
                await indexGate.WaitAsync(token);
                try
                {
                    Initialize(reader);
                    if (next == 0) return pages.Count;
                    await AdvanceAsync(reader, token);
                }
                finally { indexGate.Release(); }
            }
        }
        finally { BackgroundCounts.Release(); }
    }

    // libtiff sees original byte positions, with only the selected root/next pointers overlaid.
    // The underlying local/SFTP stream is seekable; no whole-file transfer, pixel-size cutoff or browser canvas.
    private sealed class PageStream(Stream input, CancellationToken token) : Stream
    {
        private readonly List<(long Offset, byte[] Bytes)> patches = [];
        private long position;
        public void AddPatch(long offset, byte[] bytes) => patches.Add((offset, bytes));
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => position; set => position = value >= 0 ? value : throw new IOException("Invalid TIFF seek."); }
        public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested(); cancellationToken.ThrowIfCancellationRequested();
            var start = position;
            if (input.Position != start) input.Seek(start, SeekOrigin.Begin);
            var done = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            foreach (var patch in patches)
            {
                var begin = Math.Max(start, patch.Offset); var end = Math.Min(start + done, patch.Offset + patch.Bytes.Length);
                if (end > begin) patch.Bytes.AsMemory(checked((int)(begin - patch.Offset)), checked((int)(end - begin))).CopyTo(buffer.Slice(checked((int)(begin - start))));
            }
            position += done; return done;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
