using System.Runtime.InteropServices;

namespace WfSysMon;

abstract record InputEvent;
sealed record KeyInput(ConsoleKey Key, char Char) : InputEvent;
sealed record MouseInput(int X, int Y, bool Click, int Wheel) : InputEvent;

// Teclado e mouse pela API de console do Windows. Tambem aceita cliques que cheguem como sequencias
// SGR (ESC [ < b ; x ; y M), porque alguns terminais os entregam assim. Todo comando tem atalho de
// teclado, entao o app continua utilizavel se o terminal nao repassar o mouse.
sealed class InputReader : IDisposable
{
    const uint EnableProcessedInput = 0x1, EnableWindowInput = 0x8, EnableMouseInput = 0x10, EnableExtendedFlags = 0x80;
    const ushort KeyEvent = 1, MouseEvent = 2;
    const uint MouseWheeled = 0x4, MouseMoved = 0x1, LeftButton = 0x1;

    readonly nint _handle = GetStdHandle(-10);
    readonly uint _savedMode;
    readonly bool _ready;
    readonly System.Text.StringBuilder _sequence = new();
    bool _leftDown;

    public InputReader()
    {
        // Sem ENABLE_QUICK_EDIT: com ela, o clique seleciona texto e nao chega ao app.
        _ready = GetConsoleMode(_handle, out _savedMode)
                 && SetConsoleMode(_handle, EnableExtendedFlags | EnableMouseInput | EnableWindowInput | EnableProcessedInput);
    }

    public List<InputEvent> Poll()
    {
        var events = new List<InputEvent>();
        if (_ready)
        {
            while (GetNumberOfConsoleInputEvents(_handle, out var count) && count > 0)
            {
                var buffer = new InputRecord[Math.Min(count, 64)];
                if (!ReadConsoleInputW(_handle, buffer, (uint)buffer.Length, out var read)) break;
                for (var i = 0; i < read; i++) Translate(buffer[i], events);
            }
        }
        else
        {
            try
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    events.Add(new KeyInput(key.Key, key.KeyChar));
                }
            }
            catch (InvalidOperationException) { /* entrada redirecionada: sem teclado */ }
        }

        // ESC sozinho, sem o restante de uma sequencia: e a tecla Esc.
        if (_sequence.Length == 1)
        {
            _sequence.Clear();
            events.Add(new KeyInput(ConsoleKey.Escape, '\u001b'));
        }
        return events;
    }

    void Translate(InputRecord record, List<InputEvent> events)
    {
        if (record.EventType == KeyEvent && record.Key.KeyDown != 0)
        {
            var c = (char)record.Key.UnicodeChar;
            if (_sequence.Length > 0 || c == '\u001b')
            {
                _sequence.Append(c);
                ParseSequence(events);
                return;
            }
            events.Add(new KeyInput((ConsoleKey)record.Key.VirtualKeyCode, c));
        }
        else if (record.EventType == MouseEvent)
        {
            var m = record.Mouse;
            var y = m.Y - WindowTop();
            if ((m.EventFlags & MouseWheeled) != 0)
            {
                events.Add(new MouseInput(m.X, y, false, (int)m.ButtonState < 0 ? -1 : 1));
            }
            else if ((m.EventFlags & MouseMoved) == 0)
            {
                var down = (m.ButtonState & LeftButton) != 0;
                if (down && !_leftDown) events.Add(new MouseInput(m.X, y, true, 0));
                _leftDown = down;
            }
        }
    }

    void ParseSequence(List<InputEvent> events)
    {
        var s = _sequence.ToString();
        if (s.Length < 2) return;
        if (s[1] != '[')
        {
            _sequence.Clear();
            events.Add(new KeyInput(ConsoleKey.Escape, '\u001b'));
            return;
        }
        if (s.Length < 3) return;

        if (s[2] != '<')
        {
            _sequence.Clear();
            if (s[2] == 'A') events.Add(new KeyInput(ConsoleKey.UpArrow, '\0'));
            else if (s[2] == 'B') events.Add(new KeyInput(ConsoleKey.DownArrow, '\0'));
            return;
        }

        var last = s[^1];
        if (last is not ('M' or 'm')) return;

        _sequence.Clear();
        var parts = s[3..^1].Split(';');
        if (parts.Length == 3 && int.TryParse(parts[0], out var button) && int.TryParse(parts[1], out var x) && int.TryParse(parts[2], out var y))
        {
            if (button == 64) events.Add(new MouseInput(x - 1, y - 1, false, 1));
            else if (button == 65) events.Add(new MouseInput(x - 1, y - 1, false, -1));
            else if (button == 0 && last == 'M') events.Add(new MouseInput(x - 1, y - 1, true, 0));
        }
    }

    static int WindowTop()
    {
        try { return Console.WindowTop; }
        catch (IOException) { return 0; }
    }

    public void Dispose()
    {
        if (_ready) SetConsoleMode(_handle, _savedMode);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;  // ushort: char seria convertido para 1 byte na chamada nativa
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseRecord
    {
        public short X;
        public short Y;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyRecord Key;
        [FieldOffset(4)] public MouseRecord Mouse;
    }

    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int id);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll")] static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool ReadConsoleInputW(nint handle, [Out] InputRecord[] buffer, uint length, out uint read);
}
