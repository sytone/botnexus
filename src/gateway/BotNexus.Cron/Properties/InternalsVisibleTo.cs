using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BotNexus.Cron.Tests")]

// #2810: the REST controller's definition-update path recomputes NextRunAt, so it must use the
// same timezone resolver (#2748) and the same DST-transition policy as the scheduler that will
// actually fire the job. It previously carried its own private copy of both. Widening these two
// types to public would publish scheduling internals on the extension-facing surface; granting
// the host assembly access keeps the single definition without doing that.
[assembly: InternalsVisibleTo("BotNexus.Gateway.Api")]

// #4732: the liveness dispatch-freshness probe must size each job's grace from the SAME effective
// timeout the scheduler arms (including 0 = unlimited), not a private copy of the metadata rules.
[assembly: InternalsVisibleTo("BotNexus.Gateway")]
