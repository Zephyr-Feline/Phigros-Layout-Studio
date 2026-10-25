using Avalonia;
using Avalonia.Headless;

namespace Editor.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0) LocalFontLibrary.ConfigureVerificationDirectory();
        if (args.Length > 0 && args[0] == "--font-evidence")
            return FontRestartEvidence.Run(args, Console.Out, Console.Error, InitializeHeadless);
        if (args.Length > 0 && args[0] == "--render")
            return RenderCommand.Run(args, Console.Out, Console.Error, InitializeHeadless);
        if (args.Length > 0 && args[0] == "--performance")
        {
            if (args.Length is not (2 or 3) || string.IsNullOrWhiteSpace(args[1]) ||
                args[1].StartsWith("--", StringComparison.Ordinal) ||
                args.Length == 3 && args[2] != "--large-resources")
            {
                Console.Error.WriteLine("Usage: --performance <output-directory> [--large-resources]");
                Console.Error.WriteLine("--large-resources requires a new or empty directory and adds actual large-image/output reliability checks.");
                return 2;
            }
            try
            {
                InitializeHeadless();
                _ = LocalFontLibrary.Default;
                return PerformanceProbe.Run(args[1], args.Length == 3);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"PERFORMANCE PROBE FAILED: {error.Message}");
                return 1;
            }
        }
        if (args.Length == 3 && args[0] == "--recovery-probe")
            return RecoveryProbe.Run(args[1], args[2], Console.Out, Console.Error);
        if (args.Length == 2 && args[0] == "--render-batch")
            return ProjectRenderBatchCommand.Run(args[1], Console.Out, Console.Error, InitializeHeadless);
        if (args.Length > 0 && args[0] == "--verify-portable")
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: --verify-portable <new-output-directory>");
                return 2;
            }
            try { return PortableRuntimeVerification.Run(args[1]); }
            catch (Exception error) { Console.Error.WriteLine($"RUNTIME VERIFICATION FAILED: {error.Message}"); return 1; }
        }
        if (args.Length > 0 && args[0] == "--compat-evidence")
        {
            if (args.Length is not (3 or 4))
            {
                Console.Error.WriteLine("Usage: --compat-evidence <new-output-directory> <reference-renderer-exe> [local-corpus]");
                return 2;
            }
            try
            {
                InitializeHeadless();
                return CompatibilityEvidence.Run(args[1], args[2], args.Length == 4 ? args[3] : null);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"COMPATIBILITY EVIDENCE FAILED: {error.Message}");
                return 1;
            }
        }
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine(App.ProductDisplayName);
            Console.WriteLine(RenderCommand.Usage);
            Console.WriteLine("Input: .ppgproj project or another project source recognized by the editor.");
            Console.WriteLine("--width sets PNG width; height follows the crop ratio when --crop is set, otherwise the canvas ratio.");
            Console.WriteLine("--crop x,y,width,height renders a logical rectangle inside the document canvas.");
            Console.WriteLine("Without --width: full export uses canvas dimensions; crop width rounds to an integer (ties to even), and height follows the crop ratio.");
            Console.WriteLine("Batch: --render-batch <plan.json> (formatVersion 1 or 2, outputParent, projects, tasks; format version 2 includes qualityPolicy).");
            Console.WriteLine("Warnings and conversion differences go to stderr. --block-low-resolution blocks LowResolutionArtwork warnings; other warnings remain advisory.");
            Console.WriteLine("Existing output PNGs are replaced atomically. Input projects are never rewritten.");
            Console.WriteLine("Exit codes: 0 success, 1 read/render/write/quality-policy failure, 2 invalid arguments.");
            Console.WriteLine("Internal candidate verification: --verify-portable <new-output-directory>");
            Console.WriteLine("Internal measurement: --performance <output-directory> [--large-resources] (large-resource evidence requires a new or empty directory).");
            return 0;
        }
        if (args.Length > 0 && args[0] == "--verify-reopen" && args.Length != 4)
        {
            Console.Error.WriteLine("Usage: --verify-reopen <project> <opaque-png> <transparent-png>");
            return 1;
        }
        if (args.Length >= 2 && args[0] is "--probe" or "--gates" or "--legacy" or "--compat" or "--verify-reopen")
        {
            InitializeHeadless();
            _ = LocalFontLibrary.Default;
            if (args[0] == "--verify-reopen")
            {
                try
                {
                    ProjectReopenProbe.Verify(args[1], args[2], args[3]);
                    Console.WriteLine($"REOPEN PASS: {args[1]} (opaque/transparent PNG; source unchanged)");
                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"REOPEN FAILED: {error.Message}");
                    return 1;
                }
            }
            return args[0] switch
            {
                "--gates" => ProbeRunner.RunGates(args[1]),
                "--legacy" => LegacyProbe.Run(args[1]),
                "--compat" => CompatibilityProbe.Run(args[1], args.Length > 2 ? args[2] : null),
                _ => ProbeRunner.Run(args[1])
            };
        }
        if (args.Length != 0)
        {
            Console.Error.WriteLine("Unknown or incomplete command. Use --help for PNG export usage.");
            return 2;
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    private static void InitializeHeadless() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .SetupWithoutStarting();
}
