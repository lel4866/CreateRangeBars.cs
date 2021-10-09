//
// static class CommandLine
// Process CreateRangeBars command line
//

using System;

namespace CreateRangeBars;

static class CommandLine {
    internal static void ProcessCommandLineArguments(string[] args) {
        string? arg_name = null;

        foreach (string arg in args) {
            if (arg_name == null) {
                switch (arg) {
                    case "-v":
                    case "--version":
                        Console.WriteLine(Program.version);
                        break;
                    case "-r":
                    case "--replace":
                        Program.update_only = false;
                        Console.WriteLine(Program.version);
                        break;
                    case "-s":
                    case "--symbol":
                        arg_name = "-s";
                        break;
                    case "-h":
                    case "--help":
                        Console.WriteLine(Program.version);
                        Console.WriteLine("Create range bar files from csv tick files");
                        Console.WriteLine("Command line arguments:");
                        Console.WriteLine("    --version, -v : display version number");
                        Console.WriteLine("    --update, -u  : only process files input directory which do not have corresponding file in output directory");
                        Console.WriteLine("    --symbol, -s  : futures contract symbol; i.e. for CME SP500 e-mini: ES");
                        break;

                    default:
                        Console.WriteLine("Invalid command line argument: " + arg);
                        System.Environment.Exit(-1);
                        break;
                }
            }
            else {
                switch (arg_name) {
                    case "-s":
                        if (Program.futures_root.Length > 3) {
                            Console.WriteLine("Invalid futures contract symbol: " + arg);
                            System.Environment.Exit(-1);
                        }
                        Program.futures_root = arg.ToUpper();
                        break;
                }
                arg_name = null;
            }
        }
    }
}
