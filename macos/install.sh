#!/bin/sh
# Build a release binary, copy it to ~/.local/bin/mas, and (optionally) register the login LaunchAgent.
#   ./install.sh            build + install binary
#   ./install.sh --agent    also start at login and launch the menu bar agent now
set -e
cd "$(dirname "$0")"
swift build -c release --arch arm64
mkdir -p "$HOME/.local/bin"
cp .build/release/mas "$HOME/.local/bin/mas"
xattr -d com.apple.quarantine "$HOME/.local/bin/mas" 2>/dev/null || true
echo "Installed $HOME/.local/bin/mas"
case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "Note: add ~/.local/bin to PATH (e.g. echo 'export PATH=\$HOME/.local/bin:\$PATH' >> ~/.zshrc)";; esac
if [ "$1" = "--agent" ]; then
  "$HOME/.local/bin/mas" install-agent
fi
