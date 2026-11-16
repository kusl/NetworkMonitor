namespace NetworkMonitor.Core.Display;

public sealed class LiveConsole(TextWriter output, bool ansiEnabled)
{
    private const string SaveCursor = "\x1b[s";
    private const string RestoreCursor = "\x1b[u";
    private const string EraseToEndOfScreen = "\x1b[J";

    private readonly Lock _gate = new();
    private bool _transientParked;

    public LiveConsole()
        : this(Console.Out, !Console.IsOutputRedirected)
    {
    }

    public bool AnsiEnabled => ansiEnabled;

    public void WriteTransientLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ClearParkedLine();
            if (ansiEnabled)
            {
                output.Write(SaveCursor);
                output.Write(text);
                _transientParked = true;
            }
            else
            {
                output.Write(text);
                output.Write('\n');
            }

            output.Flush();
        }
    }

    public void WriteBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ClearParkedLine();
            output.Write(text);
            if (!text.EndsWith('\n'))
            {
                output.Write('\n');
            }

            output.Flush();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ClearParkedLine();
            output.Flush();
        }
    }

    private void ClearParkedLine()
    {
        if (_transientParked)
        {
            output.Write(RestoreCursor);
            output.Write(EraseToEndOfScreen);
            _transientParked = false;
        }
    }
}
