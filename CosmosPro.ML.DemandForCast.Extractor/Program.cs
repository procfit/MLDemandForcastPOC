namespace CosmosPro.ML.DemandForCast.Extractor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Sem argumentos continua sendo a interface gráfica: ela é o produto para
        // o comprador da farmácia, e o modo linha de comando é o acessório.
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return CliExitCode.Sucesso;
        }

        return ExtractorCli.Execute(args);
    }
}
