namespace KillerShot.CliHost;

/// <summary>Console entry for KillerShot.Cli.exe. The commands live in KillerShot.exe, so the
/// console build and <c>KillerShot.exe --cli</c> always behave the same.</summary>
internal static class CliEntry
{
    [STAThread]
    private static int Main(string[] args) => KillerShot.Cli.Program.RunConsole(args);
}
