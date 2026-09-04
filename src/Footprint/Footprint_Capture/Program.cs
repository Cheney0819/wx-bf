using Footprint.Worker;

return await new CaptureWorkerHost(
    new CaptureRuntimeAssemblyWorker(),
    new CurrentProcessPriorityController())
    .RunAsync(args, CancellationToken.None);
