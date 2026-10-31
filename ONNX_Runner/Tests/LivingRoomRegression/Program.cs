string? output = args.Length > 0 ? args[0] : null;
int result = LivingRoomChecks.Run(output);
return result == 0 ? PipelineTailChecks.Run(output) : result;
