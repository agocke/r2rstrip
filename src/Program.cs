using R2RStrip;

public class Program
{
    public static int Main(string[] args)
    {
        bool verbose = args.Contains("--verbose") || args.Contains("-v");
        var fileArgs = args.Where(a => !a.StartsWith("-")).ToArray();

        if (fileArgs.Length != 2)
        {
            Console.WriteLine("Usage: r2rstrip [options] <input-r2r-file> <output-file>");
            Console.WriteLine("Rebuilds an R2R assembly as IL-only with correct offsets");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -v, --verbose    Show detailed progress information");
            return 1;
        }

        string inputFile = fileArgs[0];
        string outputFile = fileArgs[1];

        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"Error: Input R2R file '{inputFile}' not found");
            return 1;
        }

        try
        {
            R2RStripper.Strip(inputFile, outputFile, verbose);
            Console.WriteLine($"Successfully rebuilt '{inputFile}' as IL-only assembly: '{outputFile}'");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }
}
