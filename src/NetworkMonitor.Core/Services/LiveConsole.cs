namespace NetworkMonitor.Core.Services;

/// <summary>
/// The single, synchronized owner of every write to standard output.
///
/// WHY THIS EXISTS:
///
/// The status display parks a "live" status line on screen WITHOUT a trailing
/// newline so the next healthy cycle can overwrite it in place. Separately, the
/// logging pipeline writes log records. If those two writers touch the console
/// independently there is no ordering guarantee, and the default console logger
/// writes from its own background thread. The result is mangled output such as:
///
///     ● Excellent  Router: 2ms Internet: 18ms [20:09:09]info: NetworkMonitor…
///     ● Excellent  Router: 2ms Internet: info: NetworkMonitor…   ← cut mid-line
///
/// Routing BOTH the status display and the logger through this one object fixes
/// that at the source: a single lock serializes every write, each status line
/// is emitted as one atomic string, and whenever a log record (or any permanent
/// block) is about to print, the parked status line is erased first so the log
/// always lands on its own fresh line. The status line is then redrawn by the
/// next cycle. Every timestamped line ends up on its own line.
///
/// REDIRECTED OUTPUT:
///
/// When standard output is not a terminal (piped to a file, captured by CI, …)
/// cursor movement and colors are meaningless and would just be garbage in the
/// file. In that case ANSI is disabled: transient lines become ordinary
/// newline-terminated lines, so a captured log stays clean and greppable.
/// </summary>
public sealed class LiveConsole
{
    // Cursor control (ECMA-48 / ANSI X3.64). Only emitted for real terminals.
    private const string SaveCursor = "\x1b[s";
    private const string RestoreCursor = "\x1b[u";
    private const string EraseToEndOfScreen = "\x1b[J";

    private readonly Lock _gate = new();
    private readonly TextWriter _out;
    private readonly bool _ansiEnabled;

    // True while a transient status line is parked on screen without a trailing
    // newline (ANSI mode only). Guarded by _gate.
    private bool _transientParked;

    /// <summary>
    /// Production constructor: writes to <see cref="Console.Out"/> and enables
    /// ANSI cursor control and colors only when stdout is an interactive
    /// terminal (never when redirected to a file or pipe).
    /// </summary>
    public LiveConsole()
        : this(Console.Out, !Console.IsOutputRedirected)
    {
    }

    /// <summary>
    /// Test/advanced constructor allowing an explicit writer and ANSI mode.
    /// </summary>
    public LiveConsole(TextWriter output, bool ansiEnabled)
    {
        ArgumentNullException.ThrowIfNull(output);
        _out = output;
        _ansiEnabled = ansiEnabled;
    }

    /// <summary>
    /// Whether ANSI cursor control and colors should be emitted. False when
    /// output is redirected; consumers use this to strip color codes.
    /// </summary>
    public bool AnsiEnabled => _ansiEnabled;

    /// <summary>
    /// Writes a status line intended to be overwritten by the next cycle.
    /// In a terminal the cursor is saved so the line can be erased later; when
    /// redirected the line is simply terminated with a newline.
    /// </summary>
    public void WriteTransientLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ClearParkedLine();

            if (_ansiEnabled)
            {
                _out.Write(SaveCursor);
                _out.Write(text);
                _out.Flush();
                _transientParked = true;
            }
            else
            {
                _out.Write(text);
                _out.Write('\n');
                _out.Flush();
            }
        }
    }

    /// <summary>
    /// Writes a block of permanent, scrolling output (problem details, verbose
    /// per-target lines, or a log record). Any parked transient line is erased
    /// first so this block starts on its own line, and a trailing newline is
    /// guaranteed so the following output also starts cleanly.
    /// </summary>
    public void WriteBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ClearParkedLine();
            _out.Write(text);

            if (!EndsWithNewline(text))
            {
                _out.Write('\n');
            }

            _out.Flush();
        }
    }

    /// <summary>
    /// Erases any parked transient line and clears the parked state. Used on
    /// shutdown so the final messages are not overwritten.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            ClearParkedLine();
        }
    }

    private void ClearParkedLine()
    {
        if (_transientParked)
        {
            _out.Write(RestoreCursor);
            _out.Write(EraseToEndOfScreen);
            _transientParked = false;
        }
    }

    private static bool EndsWithNewline(string text) =>
        text.Length > 0 && text[^1] == '\n';
}
