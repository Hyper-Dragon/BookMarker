// BookMarkerOptions reads the process environment at startup and the test host starts the
// real pipeline, so tests that touch either must not run concurrently. The suite is small;
// serialising it is cheaper than making the configuration surface injectable for tests only.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
