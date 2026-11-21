using System.Text;

namespace OpenWebtau.Services;

/// <summary>
/// Unpacks a voicebank the server extracted (mcp/server.mjs streams it as a
/// PAX tar of &lt;dir&gt;/...) into the singers folder. Plain C#, so the tests
/// compile it too.
/// </summary>
public static class BankTar {
    /// <returns>Files written.</returns>
    public static async Task<int> ExtractAsync(Stream tar, string singersRoot, string dir,
            Action<long>? progress = null, CancellationToken token = default) {
        if (dir.Length == 0 || dir.StartsWith('.') || dir.IndexOfAny(new[] { '/', '\\' }) >= 0) {
            throw new ArgumentException($"bad bank folder '{dir}'");
        }
        var root = Path.GetFullPath(Path.Combine(singersRoot, dir));
        var input = new CountingStream(tar, progress);
        var header = new byte[Block];
        var buffer = new byte[64 * 1024];
        string? paxPath = null;
        int files = 0;
        // System.Formats.Tar throws PlatformNotSupported in browser wasm, so this
        // reads the ustar + PAX subset mcp/server.mjs writes (and GNU tar's
        // plain files), asynchronously since the response stream is browser-backed.
        while (true) {
            if (!await ReadFull(input, header, Block, token)) return files;   // no end blocks: done
            if (header.All(b => b == 0)) return files;
            long size = Octal(header, 124, 12);
            char type = (char)header[156];
            long padded = (size + Block - 1) / Block * Block;
            if (type is 'x' or 'g') {
                var data = new byte[padded];
                if (!await ReadFull(input, data, (int)padded, token)) throw new EndOfStreamException();
                if (type == 'x') paxPath = PaxValue(data.AsSpan(0, (int)size), "path");
                continue;
            }
            string name = paxPath ?? UstarName(header);
            paxPath = null;
            if (type is '0' or '\0') {
                var path = Path.GetFullPath(Path.Combine(singersRoot, name));
                // Only this bank's own folder; a crafted name must not write elsewhere.
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
                    throw new InvalidDataException($"tar entry outside {dir}/: {name}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var file = File.Create(path)) {
                    await Copy(input, file, size, buffer, token);
                }
                await Skip(input, padded - size, buffer, token);
                files++;
            } else {
                await Skip(input, padded, buffer, token);   // directories, links: nothing to write
            }
        }
    }

    const int Block = 512;

    static async Task<bool> ReadFull(Stream s, byte[] buf, int count, CancellationToken token) {
        int got = 0;
        while (got < count) {
            int n = await s.ReadAsync(buf.AsMemory(got, count - got), token);
            if (n == 0) {
                if (got == 0) return false;
                throw new EndOfStreamException("truncated tar");
            }
            got += n;
        }
        return true;
    }

    static async Task Copy(Stream from, Stream to, long count, byte[] buf, CancellationToken token) {
        while (count > 0) {
            int n = await from.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), token);
            if (n == 0) throw new EndOfStreamException("truncated tar");
            await to.WriteAsync(buf.AsMemory(0, n), token);
            count -= n;
        }
    }

    static Task Skip(Stream from, long count, byte[] buf, CancellationToken token) =>
        Copy(from, Stream.Null, count, buf, token);

    static long Octal(byte[] h, int offset, int length) {
        long v = 0;
        for (int i = offset; i < offset + length; i++) {
            byte b = h[i];
            if (b == 0 || b == (byte)' ') { if (v != 0) break; continue; }
            if (b < '0' || b > '7') throw new InvalidDataException("bad tar header");
            v = v * 8 + (b - '0');
        }
        return v;
    }

    static string Field(byte[] h, int offset, int length) {
        int end = Array.IndexOf(h, (byte)0, offset, length);
        return Encoding.UTF8.GetString(h, offset, (end < 0 ? offset + length : end) - offset);
    }

    static string UstarName(byte[] h) {
        var name = Field(h, 0, 100);
        var prefix = Field(h, 345, 155);
        return prefix.Length > 0 ? prefix + "/" + name : name;
    }

    /// One key from PAX records ("<len> key=value\n" ...), or null.
    static string? PaxValue(ReadOnlySpan<byte> data, string key) {
        while (data.Length > 0) {
            int space = data.IndexOf((byte)' ');
            if (space <= 0 || !int.TryParse(Encoding.ASCII.GetString(data[..space]), out int len) || len <= space || len > data.Length) {
                return null;
            }
            var record = Encoding.UTF8.GetString(data[(space + 1)..(len - 1)]);
            int eq = record.IndexOf('=');
            if (eq > 0 && record[..eq] == key) return record[(eq + 1)..];
            data = data[len..];
        }
        return null;
    }

    /// Reports bytes read so far, for the progress bar.
    sealed class CountingStream : Stream {
        readonly Stream inner;
        readonly Action<long>? progress;
        long read;

        public CountingStream(Stream inner, Action<long>? progress) {
            this.inner = inner;
            this.progress = progress;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
            int n = await inner.ReadAsync(buffer, token);
            read += n;
            progress?.Invoke(read);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override int Read(byte[] buffer, int offset, int count) {
            int n = inner.Read(buffer, offset, count);
            read += n;
            progress?.Invoke(read);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
