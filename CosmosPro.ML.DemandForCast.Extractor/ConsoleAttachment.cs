using System.Runtime.InteropServices;
using System.Text;

namespace CosmosPro.ML.DemandForCast.Extractor;

/// <summary>
/// O extrator é <c>WinExe</c> (subsistema Windows) para o usuário de GUI não ver
/// um console piscando — mas isso é justamente o que faz <c>Console.WriteLine</c>
/// não aparecer quando alguém o chama de um terminal. Anexar ao console do
/// processo pai devolve a saída ao terminal de quem chamou.
/// <para>
/// O handle padrão só é trocado por <c>CONOUT$</c> quando o herdado é inválido:
/// se o chamador redirecionou (<c>&gt; saida.txt</c>, ou um pipe de teste
/// automatizado), o handle herdado é válido e apontar para o console destruiria
/// o redirecionamento.
/// </para>
/// </summary>
internal sealed class ConsoleAttachment : IDisposable
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private static readonly IntPtr InvalidHandle = new(-1);

    private readonly bool _anexado;

    private ConsoleAttachment(bool anexado) => _anexado = anexado;

    public static ConsoleAttachment Attach()
    {
        var anexado = AttachConsole(AttachParentProcess);

        if (anexado)
        {
            RedirecionarSeSemHandle(StdOutputHandle);
            RedirecionarSeSemHandle(StdErrorHandle);
        }

        // Sem isto o texto acentuado sai como mojibake na code page OEM herdada.
        TentarUtf8();
        ReabrirEscritores();

        return new ConsoleAttachment(anexado);
    }

    public void Dispose()
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (IOException)
        {
        }

        if (_anexado) FreeConsole();
    }

    private static void RedirecionarSeSemHandle(int qual)
    {
        var atual = GetStdHandle(qual);
        if (atual != IntPtr.Zero && atual != InvalidHandle) return;

        var console = CreateFileW("CONOUT$", GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (console != InvalidHandle) SetStdHandle(qual, console);
    }

    private static void TentarUtf8()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
        }
    }

    private static void ReabrirEscritores()
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
    }

    // DllImport e não LibraryImport: o gerador de origem do LibraryImport exige
    // AllowUnsafeBlocks no projeto inteiro, preço alto demais por cinco chamadas.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
