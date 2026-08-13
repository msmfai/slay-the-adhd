// The weave test does process-wide Harmony.PatchAll on the loaded mod assembly; running
// it concurrently with the relic/card/real-state tests corrupts parallel collections.
// Serialize the whole assembly so global patching is isolated (and unpatched) each run.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
