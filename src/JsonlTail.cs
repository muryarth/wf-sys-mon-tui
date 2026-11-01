namespace WfSysMon;

// Le arquivos JSONL de forma incremental: so o que foi acrescentado desde a ultima leitura,
// linha a linha, sem carregar o arquivo inteiro. Quem chama deve tolerar a mesma linha duas
// vezes, porque um arquivo reescrito e lido de novo desde o inicio.
sealed class JsonlTail
{
    readonly Dictionary<string, long> _offsets = [];

    public void Reset() => _offsets.Clear();

    public void Read(string path, Action<ReadOnlyMemory<byte>> onLine)
    {
        var length = new FileInfo(path).Length;
        _offsets.TryGetValue(path, out var offset);
        if (length < offset) offset = 0;
        if (length == offset) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        using var line = new MemoryStream();
        var consumed = offset;  // linha ainda sem '\n' fica para a proxima leitura
        int read;
        try
        {
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                var span = buffer.AsSpan(0, read);
                while (true)
                {
                    var newline = span.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        line.Write(span);
                        break;
                    }

                    line.Write(span[..newline]);
                    consumed += line.Length + 1;
                    onLine(line.GetBuffer().AsMemory(0, (int)line.Length));
                    line.SetLength(0);
                    span = span[(newline + 1)..];
                }
            }
        }
        finally
        {
            // Mesmo com falha: uma linha problematica nao pode travar a leitura do resto do arquivo.
            _offsets[path] = consumed;
        }
    }
}
