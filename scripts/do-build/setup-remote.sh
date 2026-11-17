#!/bin/bash
# Runs ON the build droplet (Ubuntu 24.04, root): Rust + cargo-xwin (MSVC cross) + Wine for a smoke test.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive NEEDRESTART_SUSPEND=1
t=$(date +%s); step() { n=$(date +%s); echo "STEP $1 $((n - t))"; t=$n; }
cloud-init status --wait >/dev/null 2>&1 || true
apt-get update -qq
apt-get install -y -qq build-essential clang lld llvm cmake git curl ca-certificates pkg-config \
    wine64 xvfb imagemagick >/dev/null
step apt
curl -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable >/dev/null 2>&1
. "$HOME/.cargo/env"
rustup target add x86_64-pc-windows-msvc >/dev/null
rustc --version
step rustup
cargo install --locked cargo-xwin >/dev/null 2>&1
cargo xwin --version
step cargo-xwin
echo SETUP-DONE
