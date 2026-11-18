#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Il2CppDumper.Diff
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 1)
            {
                if (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase))
                {
                    ShowVersion();
                    return 0;
                }
                if (string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[0], "/?", StringComparison.OrdinalIgnoreCase))
                {
                    ShowHelp();
                    return 0;
                }
            }

            var isDriftMode = false;
            var isLegacyMode = false;
            var positionalArgs = new List<string>();

            foreach (var arg in args)
            {
                if (string.Equals(arg, "--drift", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-d", StringComparison.OrdinalIgnoreCase))
                {
                    isDriftMode = true;
                }
                else if (string.Equals(arg, "--legacy", StringComparison.OrdinalIgnoreCase))
                {
                    isLegacyMode = true;
                }
                else
                {
                    positionalArgs.Add(arg);
                }
            }

            if (positionalArgs.Count != 3 || (isDriftMode && isLegacyMode))
            {
                ShowHelp();
                Environment.ExitCode = 2;
                return 2;
            }

            var left = positionalArgs[0];
            var right = positionalArgs[1];
            var output = positionalArgs[2];

            int exitCode;
            if (isLegacyMode)
            {
                exitCode = LegacyDiff.Execute(left, right, output, Console.Out);
            }
            else if (isDriftMode)
            {
                exitCode = FieldOffsetDiff.Execute(left, right, output, Console.Out);
            }
            else
            {
                exitCode = DumpDiffer.Execute(left, right, output, Console.Out);
            }

            Environment.ExitCode = exitCode;
            return exitCode;
        }

        static void ShowVersion()
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "0.1.0";
            Console.WriteLine($"Il2CppDumper.Diff {version}");
        }

        static void ShowHelp()
        {
            Console.WriteLine("usage: Il2CppDumper.Diff <left> <right> <output>");
            Console.WriteLine("       Il2CppDumper.Diff --drift <left> <right> <output>");
            Console.WriteLine("       Il2CppDumper.Diff --legacy <left> <right> <output>");
            Console.WriteLine("       Il2CppDumper.Diff --help | -h");
            Console.WriteLine("       Il2CppDumper.Diff --version | -v");
            Console.WriteLine();
            Console.WriteLine("Modes:");
            Console.WriteLine("  default   Full dump diff (types, methods, strings, addresses -> dump-diff.json)");
            Console.WriteLine("  --drift   Native struct field-offset drift report (FieldOffsetDiff -> field-offset-drift.json)");
            Console.WriteLine("  --legacy  Partial type comparison using original tokens (DummyDll or dump.json -> legacy-diff.json)");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  <left>    Directory or dump.json file of the baseline build.");
            Console.WriteLine("  <right>   Directory or dump.json file of the updated build.");
            Console.WriteLine("  <output>  Destination path or directory for output JSON.");
            Console.WriteLine("  Scoped dumps must cover the same image names in both inputs.");
            Console.WriteLine("  The default diff verifies address and literal maps recorded in run-manifest.json.");
            Console.WriteLine("  Legacy mode requires at least one DummyDll input and compares types in shared images only.");
            Console.WriteLine();
            Console.WriteLine("Exit codes:");
            Console.WriteLine("  0 = no differences in compared sections / no drift");
            Console.WriteLine("  1 = differences / drift found");
            Console.WriteLine("  2 = usage/input error");
        }
    }
}
