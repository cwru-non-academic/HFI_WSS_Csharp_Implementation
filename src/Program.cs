using System.Globalization;
using System.Linq;

namespace HFI.Wss;

/// <summary>
/// Console bootstrap that wires the stimulation controller to simple CLI arguments.
/// Defaults target a local Config directory with live hardware (test mode off) unless overridden.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Entry point: parse args, start the controller, and run the interactive REPL.
    /// </summary>
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintCliUsage();
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            using var controller = new StimulationController(options);

            controller.Initialize();
            Console.WriteLine("HFI WSS stimulation controller ready.");
            Console.WriteLine($"Config: {options.ConfigPath}");
            Console.WriteLine($"Serial: {(string.IsNullOrWhiteSpace(options.SerialPort) ? "auto-detect" : options.SerialPort)}");
            Console.WriteLine($"Mode valid: {controller.isModeValid()}, Ready: {controller.Ready()}, Basic API: {controller.BasicSupported}");
            PrintCommandHelp();
            RunInteractiveLoop(controller);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Translates CLI switches into strong typed options.
    /// Defaults: test mode OFF, auto-serial, Config folder in current working dir, 5 retries, 10 ms tick.
    /// </summary>
    private static StimulationOptions ParseOptions(string[] args)
    {
        string? serial = null;
        bool testMode = false;
        int maxTries = 5;
        string configPath = Path.Combine(Environment.CurrentDirectory, "Config");
        int tickInterval = 10; // milliseconds

        foreach (var arg in args)
        {
            if (arg.StartsWith("--serial=", StringComparison.OrdinalIgnoreCase))
            {
                serial = arg[(arg.IndexOf('=') + 1)..];
            }
            else if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[(arg.IndexOf('=') + 1)..];
                if (!string.IsNullOrWhiteSpace(value))
                    configPath = Path.GetFullPath(value);
            }
            else if (arg.StartsWith("--max-retries=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[(arg.IndexOf('=') + 1)..];
                if (int.TryParse(value, out var parsed) && parsed > 0)
                    maxTries = parsed;
            }
            else if (arg.StartsWith("--tick=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[(arg.IndexOf('=') + 1)..];
                if (int.TryParse(value, out var parsed) && parsed > 0)
                    tickInterval = parsed;
            }
            else if (arg.Equals("--test", StringComparison.OrdinalIgnoreCase))
            {
                testMode = true;
            }
        }

        return new StimulationOptions
        {
            SerialPort = serial,
            TestMode = testMode,
            MaxSetupTries = maxTries,
            ConfigPath = configPath,
            TickIntervalMs = tickInterval
        };
    }

    /// <summary>
    /// Simple blocking REPL that lets operators send quick commands to the controller.
    /// CTRL+C or the "exit" command will route through this loop to ensure a clean shutdown.
    /// </summary>
    private static void RunInteractiveLoop(StimulationController controller)
    {
        var running = true;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
            Console.WriteLine();
            Console.WriteLine("Cancellation requested. Shutting down...");
        };

        while (running)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input == null) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            try
            {
                var handled = ProcessCommand(parts, controller, ref running);
                if (!handled)
                    Console.WriteLine("Unknown command. Type 'help' to list options.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Command failed: {ex.Message}");
            }
        }

        controller.StopStimulation();
        controller.Shutdown();
    }

    /// <summary>
    /// Interprets REPL tokens and dispatches to the relevant controller APIs.
    /// </summary>
    private static bool ProcessCommand(string[] parts, StimulationController controller, ref bool running)
    {
        var cmd = parts[0].ToLowerInvariant();
        switch (cmd)
        {
            case "help":
                PrintCommandHelp();
                return true;

            case "quit":
            case "exit":
                running = false;
                return true;

            case "start":
                controller.StartStimulation();
                Console.WriteLine("Stim start requested.");
                return true;

            case "stop":
                controller.StopStimulation();
                Console.WriteLine("Stim stop requested.");
                return true;

            case "status":
                Console.WriteLine($"Ready={controller.Ready()}, Started={controller.Started()}, ModeValid={controller.isModeValid()}, BasicSupported={controller.BasicSupported}");
                return true;

            case "reload-core":
                controller.LoadCoreConfigFile();
                Console.WriteLine("Core config reloaded.");
                return true;

            case "reload-params":
                if (parts.Length > 1)
                    controller.LoadParamsJson(parts[1]);
                else
                    controller.LoadParamsJson();
                Console.WriteLine("Params reloaded.");
                return true;

            case "save-params":
                controller.SaveParamsJson();
                Console.WriteLine("Params saved.");
                return true;

            case "stim":
                if (parts.Length < 3)
                {
                    Console.WriteLine("Usage: stim <finger|chX> <magnitude>");
                    return true;
                }
                if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mag))
                    throw new InvalidOperationException("Magnitude must be numeric.");
                controller.StimWithMode(parts[1], mag);
                return true;

            case "analog":
                if (parts.Length < 3)
                {
                    Console.WriteLine("Usage: analog <finger|chX> <pw> [amp] [ipi]");
                    return true;
                }

                if (!int.TryParse(parts[2], out var pw))
                    throw new InvalidOperationException("Pulse width must be numeric.");
                int amp = parts.Length > 3 && int.TryParse(parts[3], out var parsedAmp) ? parsedAmp : 3;
                int ipi = parts.Length > 4 && int.TryParse(parts[4], out var parsedIpi) ? parsedIpi : 10;
                controller.StimulateAnalog(parts[1], pw, amp, ipi);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Prints command-line options along with their defaults.</summary>
    private static void PrintCliUsage()
    {
        Console.WriteLine("HFI WSS Stim console");
        Console.WriteLine("Usage: dotnet run -- [options]");
        Console.WriteLine("Options (defaults in parentheses):");
        Console.WriteLine("  --serial=NAME       Fully qualified serial device (auto-detect).");
        Console.WriteLine("  --config=PATH       Config directory path (" + Path.Combine(Environment.CurrentDirectory, "Config") + ").");
        Console.WriteLine("  --max-retries=N     Max setup retries (5).");
        Console.WriteLine("  --tick=MS           Tick interval in milliseconds (10).");
        Console.WriteLine("  --test              Enable simulated transport (off).");
        Console.WriteLine("  --help              Show this message.");
    }

    /// <summary>Prints command help.</summary>
    private static void PrintCommandHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  help                Show this help text.");
        Console.WriteLine("  start               Broadcast StartStim.");
        Console.WriteLine("  stop                Broadcast StopStim.");
        Console.WriteLine("  stim <finger> <v>  Stim with model/params layer (normalized magnitude).");
        Console.WriteLine("  analog <finger> <pw> [amp] [ipi]  Send direct analog request.");
        Console.WriteLine("  reload-core         Reload the core config JSON.");
        Console.WriteLine("  reload-params [p]   Reload params JSON (optionally from path).");
        Console.WriteLine("  save-params         Persist params JSON.");
        Console.WriteLine("  status              Print Ready/Started/mode state.");
        Console.WriteLine("  quit|exit           Terminate the program.");
    }
}
