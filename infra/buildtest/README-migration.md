# Runner infrastructure moved

Azure provisioning, networking, image construction, deployment and image updates
are owned by [sytone/botnexus-runner](https://github.com/sytone/botnexus-runner).
Use its infrastructure guide and keep real deployment settings outside Git.

## Temporary application compatibility

The application still loads legacy result, timeout, diagnostic and source helpers
from this directory. They and their tests remain unchanged until the application
client and CI cut over. Their presence does not make this repository the owner of
the standalone image or authorize rebuilding or deploying it. The Dockerfile and
Azure provisioning entrypoint and templates have moved out.

Runner release qualification, client cutover and live application acceptance remain
separate work. This source move does not prove a usable live runner.
