using WfSysMon;
using Xunit;

// Instalar o servico exige administrador; a troca de pastas, que decide o que sobra numa falha, nao.
public class InstallFoldersTests
{
    sealed class Scenario : IDisposable
    {
        readonly TempDir _temp = new();

        public string Source => Path.Combine(_temp.Path, "source");
        public string Target => Path.Combine(_temp.Path, "wf-sys-mon");
        public string Staged => Target + ".new";
        public string Previous => Target + ".previous";
        public InstallFolders Folders { get; }

        public Scenario()
        {
            Folders = new InstallFolders(Target, moveAttempts: 1);
            Write(Source, "app.dll", "new");
            Write(Source, Path.Combine("sub", "lib.dll"), "new lib");
            Write(Source, "app.dll.abc.old", "leftover");
            Write(Target, "app.dll", "old");
            Write(Target, "removed.dll", "old");
        }

        public static void Write(string dir, string name, string text)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose() => _temp.Dispose();
    }

    [Fact]
    public void StageLeavesTheInstalledFolderUntouched()
    {
        using var s = new Scenario();
        Scenario.Write(s.Staged, "stale.dll", "from an interrupted install");

        s.Folders.Stage(s.Source);

        Assert.Equal("old", File.ReadAllText(Path.Combine(s.Target, "app.dll")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(s.Staged, "app.dll")));
        Assert.Equal("new lib", File.ReadAllText(Path.Combine(s.Staged, "sub", "lib.dll")));
        Assert.False(File.Exists(Path.Combine(s.Staged, "app.dll.abc.old")));
        Assert.False(File.Exists(Path.Combine(s.Staged, "stale.dll")));
    }

    [Fact]
    public void SwapInstallsOnlyTheNewFilesAndDiscardKeepsThem()
    {
        using var s = new Scenario();
        s.Folders.Stage(s.Source);

        s.Folders.Swap();

        Assert.Equal("new", File.ReadAllText(Path.Combine(s.Target, "app.dll")));
        Assert.False(File.Exists(Path.Combine(s.Target, "removed.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(s.Previous, "app.dll")));

        s.Folders.Discard();

        Assert.True(File.Exists(Path.Combine(s.Target, "app.dll")));
        Assert.False(Directory.Exists(s.Previous));
        Assert.False(Directory.Exists(s.Staged));
    }

    [Fact]
    public void SwapWorksOnAFirstInstall()
    {
        using var s = new Scenario();
        Directory.Delete(s.Target, recursive: true);
        s.Folders.Stage(s.Source);

        s.Folders.Swap();
        s.Folders.Restore();  // sem versao anterior, nao ha para onde voltar

        Assert.Equal("new", File.ReadAllText(Path.Combine(s.Target, "app.dll")));
    }

    [Fact]
    public void SwapFailureKeepsTheInstalledFiles()
    {
        using var s = new Scenario();
        s.Folders.Stage(s.Source);

        // Arquivo aberto, como um servico que ainda nao soltou a DLL: a pasta nao pode ser movida.
        using (File.Open(Path.Combine(s.Target, "app.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(s.Folders.Swap);
        }

        Assert.Equal("old", File.ReadAllText(Path.Combine(s.Target, "app.dll")));
        Assert.True(File.Exists(Path.Combine(s.Target, "removed.dll")));
    }

    [Fact]
    public void SwapPutsThePreviousVersionBackWhenTheNewOneCannotMove()
    {
        using var s = new Scenario();
        s.Folders.Stage(s.Source);

        // Arquivo aberto na copia nova: a anterior ja saiu do lugar e precisa voltar.
        using (File.Open(Path.Combine(s.Staged, "app.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(s.Folders.Swap);
        }

        Assert.Equal("old", File.ReadAllText(Path.Combine(s.Target, "app.dll")));
        Assert.False(Directory.Exists(s.Previous));
    }

    [Fact]
    public void RestoreBringsThePreviousVersionBack()
    {
        using var s = new Scenario();
        s.Folders.Stage(s.Source);
        s.Folders.Swap();

        s.Folders.Restore();

        Assert.Equal("old", File.ReadAllText(Path.Combine(s.Target, "app.dll")));
        Assert.True(File.Exists(Path.Combine(s.Target, "removed.dll")));
        Assert.False(Directory.Exists(s.Previous));
    }
}
