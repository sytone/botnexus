# This script installs all the needed items so agents can 
# work with the repo.

# Get OS
$os = $env:OS


# Windows Commands
if ($os -like "*Windows*") {

    # Installs use scoop.
    scoop install ripgrep
    scoop install grep

}


# Linux Commands
if ($os -like "*Linux*") {

    # Installs use apt.
    sudo apt update
    sudo apt install -y ripgrep grep

}