using Xunit;

// Database migrations and legacy service singletons are process-wide.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
