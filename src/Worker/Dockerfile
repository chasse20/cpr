ARG CPR_IMAGE=cpr:latest
FROM ${CPR_IMAGE} AS cpr

FROM node:22-bookworm
ARG INSTALL_RUST=false
ENV DEBIAN_FRONTEND=noninteractive DOTNET_ROOT=/usr/share/dotnet RUSTUP_HOME=/opt/rustup CARGO_HOME=/opt/cargo
RUN apt-get update && apt-get install -y --no-install-recommends \
    openssh-server git ca-certificates curl bash rsync libicu72 zlib1g \
    build-essential pkg-config cmake clang libssl-dev libudev-dev \
    libasound2-dev libgtk-3-dev libx11-dev libxcb1-dev libxkbcommon-dev \
    libwayland-dev libgl1 libegl1 libvulkan1 vulkan-tools procps util-linux \
    && rm -rf /var/lib/apt/lists/*
RUN npm install -g @openai/codex
RUN usermod -l coder -d /state/home/coder -s /bin/bash -p '*' node && \
    mkdir -p /etc/cpr/keys /run/sshd
RUN if [ "$INSTALL_RUST" = "true" ]; then curl -fsSL https://sh.rustup.rs -o /tmp/rustup.sh && \
    sh /tmp/rustup.sh -y --no-modify-path --profile minimal --default-toolchain stable --component rustfmt --component clippy --target wasm32-unknown-unknown && \
    rm /tmp/rustup.sh && \
    ln -s /opt/cargo/bin/cargo /usr/local/bin/cargo && \
    ln -s /opt/cargo/bin/rustc /usr/local/bin/rustc && \
    ln -s /opt/cargo/bin/rustdoc /usr/local/bin/rustdoc && \
    ln -s /opt/cargo/bin/rustup /usr/local/bin/rustup && \
    ln -s /opt/cargo/bin/cargo-fmt /usr/local/bin/cargo-fmt && \
    ln -s /opt/cargo/bin/rustfmt /usr/local/bin/rustfmt && \
    ln -s /opt/cargo/bin/cargo-clippy /usr/local/bin/cargo-clippy && \
    ln -s /opt/cargo/bin/clippy-driver /usr/local/bin/clippy-driver; fi
COPY --from=cpr /usr/share/dotnet /usr/share/dotnet
COPY --from=cpr /opt/cpr-worker /opt/cpr-worker
RUN ln -s /opt/cpr-worker/CPR.Worker /usr/local/bin/cpr-worker
ENV HOME=/state/home/coder CODEX_HOME=/state/home/coder/.codex CARGO_HOME=/state/cargo
EXPOSE 22
