// The update test's stand-in for a new Soundpad Web version (2.44, WP2; see scripts/update-swap-test.ps1). It passes
// the updater's checks that run before the swap (a PE with the release's ProductVersion, and --licenses printing the
// notices marker), and then never becomes ready, so the old companion's watchdog has to put the previous EXE back.
// A file "update-fixture.mode" next to it can say "probe-fail": then --licenses fails, as a blocked program would.
var mode = "";
try { mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-fixture.mode")).Trim(); } catch (IOException) { }
if (args.Contains("--licenses"))
{
    if (mode == "probe-fail") { Console.Error.WriteLine("update fixture: refusing to run (probe-fail)"); return 1; }
    Console.WriteLine("MIT License");
    Console.WriteLine("Copyright (c) 2017 Leppsoft");
    return 0;
}
Console.WriteLine("update fixture: started, and never ready");
Thread.Sleep(Timeout.Infinite);
return 0;
