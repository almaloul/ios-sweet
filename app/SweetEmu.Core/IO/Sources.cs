using System;
using System.IO;

namespace SweetEmu.Core.IO;

/// <summary>Random-access, read-only byte source (thread-safe reads).</summary>
public interface IByteSource : IDisposable
{
    long Length { get; }
    /// <summary>Read exactly buffer.Length bytes at offset (zero-filled past the end).</summary>
    void ReadAt(long offset, Span<byte> buffer);
}

public static class ByteSourceExtensions
{
    public static byte[] ReadBytes(this IByteSource s, long offset, int count)
    {
        var b = new byte[count];
        s.ReadAt(offset, b);
        return b;
    }

    public static Stream AsStream(this IByteSource s) => new ByteSourceStream(s);
}

/// <summary>A file opened for shared random reads.</summary>
public sealed class FileSource : IByteSource
{
    readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
    public long Length { get; }

    public FileSource(string path)
    {
        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(handle);
    }

    public void ReadAt(long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            if (offset + done >= Length)
            {
                buffer[done..].Clear();
                return;
            }
            int n = RandomAccess.Read(handle, buffer[done..], offset + done);
            if (n <= 0)
            {
                buffer[done..].Clear();
                return;
            }
            done += n;
        }
    }

    public void Dispose() => handle.Dispose();
}

/// <summary>A window [offset, offset+length) of another source.</summary>
public sealed class SubSource(IByteSource inner, long offset, long length, bool ownsInner = false) : IByteSource
{
    public long Length => length;

    public void ReadAt(long off, Span<byte> buffer)
    {
        if (off >= length)
        {
            buffer.Clear();
            return;
        }
        int n = (int)Math.Min(buffer.Length, length - off);
        inner.ReadAt(offset + off, buffer[..n]);
        buffer[n..].Clear();
    }

    public void Dispose()
    {
        if (ownsInner) inner.Dispose();
    }
}

public sealed class MemorySource(byte[] data) : IByteSource
{
    public long Length => data.Length;

    public void ReadAt(long offset, Span<byte> buffer)
    {
        buffer.Clear();
        if (offset >= data.Length) return;
        int n = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, n).CopyTo(buffer);
    }

    public void Dispose() { }
}

/// <summary>Read-only seekable Stream over an IByteSource.</summary>
public sealed class ByteSourceStream(IByteSource src) : Stream
{
    long pos;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => src.Length;
    public override long Position { get => pos; set => pos = value; }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Max(0, Math.Min(buffer.Length, src.Length - pos));
        if (n == 0) return 0;
        src.ReadAt(pos, buffer[..n]);
        pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, _ => src.Length + offset };
        return pos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
