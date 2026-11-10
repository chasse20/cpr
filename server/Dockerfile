FROM node:22-bookworm
ENV DEBIAN_FRONTEND=noninteractive RUSTUP_HOME=/opt/rustup CARGO_HOME=/opt/cargo
RUN apt-get update && apt-get install -y --no-install-recommends \
    openssh-server git ca-certificates curl jq bash rsync \
    build-essential pkg-config cmake clang libssl-dev libudev-dev \
    libasound2-dev libgtk-3-dev libx11-dev libxcb1-dev libxkbcommon-dev \
    libwayland-dev libgl1 libegl1 libvulkan1 vulkan-tools procps util-linux \
    && rm -rf /var/lib/apt/lists/*
RUN npm install -g @openai/codex
RUN usermod -l coder -d /state/home/coder -s /bin/bash -p '*' node && \
    mkdir -p /etc/cpr/keys /run/sshd
RUN curl -fsSL https://sh.rustup.rs -o /tmp/rustup.sh && \
    sh /tmp/rustup.sh -y --no-modify-path --profile minimal --default-toolchain stable && \
    rm /tmp/rustup.sh && \
    ln -s /opt/cargo/bin/cargo /usr/local/bin/cargo && \
    ln -s /opt/cargo/bin/rustc /usr/local/bin/rustc && \
    ln -s /opt/cargo/bin/rustup /usr/local/bin/rustup
ENV HOME=/state/home/coder CODEX_HOME=/state/home/coder/.codex CARGO_HOME=/state/cargo
EXPOSE 22
