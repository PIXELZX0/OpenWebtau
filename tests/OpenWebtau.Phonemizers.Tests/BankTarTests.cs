using System.Formats.Tar;
using System.Text;
using OpenWebtau.Services;
using Xunit;

namespace OpenWebtau.Phonemizers.Tests;

public class BankTarTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "banktar-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    static MemoryStream Tar(params (string Name, string Text)[] files) {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true)) {
            foreach (var (name, text) in files) {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                });
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task KeepsLongHangulNames() {
        // Over ustar's 100-byte name field, so only the PAX path record carries it.
        var deep = "한국어 음원/" + string.Concat(Enumerable.Repeat("가나다라마바사", 6)) + "/아.wav";
        var tar = Tar(("뱅크/oto.ini", "a.wav=a,0,0,0,0,0"), ("뱅크/" + deep, "wav"));
        long seen = 0;
        int files = await BankTar.ExtractAsync(tar, root, "뱅크", n => seen = n);
        Assert.Equal(2, files);
        Assert.Equal("wav", File.ReadAllText(Path.Combine(root, "뱅크", deep)));
        // TarReader stops before the two zero end blocks.
        Assert.InRange(seen, tar.Length - 1024, tar.Length);
    }

    [Fact]
    public async Task ReadsPlainUstar() {
        // No PAX records: a long ASCII path is split into ustar's prefix and name fields.
        var name = "bank/" + new string('d', 90) + "/" + new string('f', 60) + ".wav";
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Ustar, leaveOpen: true)) {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "bank/"));
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) {
                DataStream = new MemoryStream(new byte[1000]),
            });
        }
        ms.Position = 0;
        Assert.Equal(1, await BankTar.ExtractAsync(ms, root, "bank"));
        Assert.Equal(1000, new FileInfo(Path.Combine(root, name)).Length);
    }

    [Fact]
    public async Task RejectsEntriesOutsideTheBank() {
        var tar = Tar(("bank/../other/x.txt", "x"));
        await Assert.ThrowsAsync<InvalidDataException>(() => BankTar.ExtractAsync(tar, root, "bank"));
        Assert.False(File.Exists(Path.Combine(root, "other", "x.txt")));
    }

    [Fact]
    public async Task RejectsBadFolderNames() {
        await Assert.ThrowsAsync<ArgumentException>(() => BankTar.ExtractAsync(Tar(), root, ".."));
        await Assert.ThrowsAsync<ArgumentException>(() => BankTar.ExtractAsync(Tar(), root, "a/b"));
    }
}
