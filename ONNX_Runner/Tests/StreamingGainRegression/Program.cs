int total=0,failed=0;
AllocationChecks.Run(Check);StreamingPowerMatchedMixerChecks.Run(Check);
Console.WriteLine($"Streaming gain: {total-failed}/{total} checks passed");return failed==0?0:1;
void Check(string name,Action action){total++;try{action();}catch(Exception e){failed++;Console.Error.WriteLine($"FAIL {name}: {e.Message}");}}
