# Core test commands.

# Get repo root
$repoRoot = $PSScriptRoot | Split-Path -Parent | Split-Path -Parent


dotnet test $repoRoot\BotNexus.slnx --nologo --tl:off
