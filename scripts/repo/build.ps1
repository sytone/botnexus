# Core build commands.

# Get repo root
$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent


dotnet build $repoRoot\BotNexus.slnx -c Debug --nologo --tl:off