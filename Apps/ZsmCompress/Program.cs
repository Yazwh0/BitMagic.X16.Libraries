using CommandLine;

namespace ZsmCompress;

class Program
{
    class Options
    {
        [Option('i', "input", Required = true, HelpText = "Input ZSM Filename.")]
        public string InputFilename { get; set; } = "";
        [Option('o', "output", Required = true, HelpText = "Output ZSMComp Filename.")] 
        public string OutputFilename { get; set; } = "";
        [Option('b', "bank", Required = false, HelpText = "Bank where the ZSMComp will be loaded.", Default = 1)]
        public int Bank { get; set; } = 1;
        [Option('a', "address", Required = false, HelpText = "Address where the ZSMComp will be loaded.", Default = 0xa000)]
        public int Address { get; set; } = 0xa000;
    }


    static void Main(string[] args)
    {
        var options = Parser.Default.ParseArguments<Options>(args);

        if (options.Errors.Any())
        {
            return;
        }

        var arguments = options.Value;

        Console.WriteLine("ZSM Compress");

        if (File.Exists(arguments.InputFilename))
        {
            try
            {
                Console.Write($"Loading '{arguments.InputFilename}'... ");
                var inputData = File.ReadAllBytes(arguments.InputFilename);
                Console.WriteLine("Done.");

                Console.Write("Compressing... ");
                var compressedData = ZsmCompress.Compress(inputData, arguments.Bank, arguments.Address, out var dictionarySize, out var dataSize);
                Console.WriteLine("Done.");

                Console.WriteLine($"Input Size      : {inputData.Length:#,##0} bytes.");
                Console.WriteLine($"Dictionary Size : {dictionarySize:#,##0} bytes.");
                Console.WriteLine($"Data Size       : {dataSize:#,##0} bytes.");
                Console.WriteLine($"Total Size      : {dictionarySize + dataSize:#,##0} bytes. ({(dictionarySize + dataSize) / (double)inputData.Length:0.0%})");
                if (dictionarySize + dataSize > inputData.Length)
                {
                    Console.WriteLine("Warning: Output is larger than the input. Consider using an uncompressed version.");
                }

                Console.Write($"Writing '{arguments.OutputFilename}'... ");
                File.WriteAllBytes(arguments.OutputFilename, compressedData);
                Console.WriteLine("Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError processing '{arguments.InputFilename}': {ex.Message}.");
            }
        }
        else
        {
            Console.WriteLine($"Example file '{arguments.InputFilename}' not found.");
        }
    }
}
