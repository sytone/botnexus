# Runner infrastructure moved

Azure provisioning, networking, image construction, deployment, image updates and
container runtime belong to [sytone/botnexus-runner](https://github.com/sytone/botnexus-runner).
Use its infrastructure guide and keep real deployment settings outside Git.

This directory retains application release/receipt tests only. Application result
checking, bounded CORE execution and diagnostic request/proof checks live under
`scripts/repo`; they cannot create infrastructure, build images or run the image
entrypoint. See the [source and test inventory](../../docs/development/runner-infrastructure-move.md).

Source publication, acceptance, deployment and successful application execution are
separate claims. This move does not activate or qualify a live runner.
