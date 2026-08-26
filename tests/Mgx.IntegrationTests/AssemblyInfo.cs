using Xunit;

// MgxCmdletBase, ResiliencePipelineFactory, MgxTelemetryCollector and GraphBatchClient all hold
// process-wide static state that the cmdlet-hosting tests inject into and reset. Running any two
// classes at once lets one clear the transport another is mid-request against, which surfaces as
// "Not connected to Microsoft Graph".
[assembly: CollectionBehavior(DisableTestParallelization = true)]
