# Runner infrastructure moved

Azure provisioning, networking, image/runtime source, image creation and updates,
and infrastructure validation are owned by [sytone/botnexus-runner](https://github.com/sytone/botnexus-runner).
Use that repository's infrastructure guide and runner-owned maintenance commands.
Keep deployment settings and credentials outside Git.

This application repository no longer owns the runner infrastructure. Container
job execution and application workflow integration are separate work. This move
does not claim a live Azure deployment or successful application execution.
