# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY src/Worker/CPR.Worker.csproj src/Worker/
COPY src/Protocol/CPR.Protocol.csproj src/Protocol/
RUN dotnet restore src/Worker/CPR.Worker.csproj
COPY src/Worker/ src/Worker/
COPY src/Protocol/ src/Protocol/
RUN dotnet publish src/Worker/CPR.Worker.csproj -c Release -o /published --no-restore -p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/runtime:10.0.14 AS runtime

FROM node:22-bookworm AS final
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
RUN curl -fsSL https://sh.rustup.rs -o /tmp/rustup.sh && \
    sh /tmp/rustup.sh -y --no-modify-path --profile minimal --default-toolchain stable --component rustfmt --component clippy --target wasm32-unknown-unknown && \
    rm /tmp/rustup.sh && \
    ln -s /opt/cargo/bin/cargo /usr/local/bin/cargo && \
    ln -s /opt/cargo/bin/rustc /usr/local/bin/rustc && \
    ln -s /opt/cargo/bin/rustdoc /usr/local/bin/rustdoc && \
    ln -s /opt/cargo/bin/rustup /usr/local/bin/rustup && \
    ln -s /opt/cargo/bin/cargo-fmt /usr/local/bin/cargo-fmt && \
    ln -s /opt/cargo/bin/rustfmt /usr/local/bin/rustfmt && \
    ln -s /opt/cargo/bin/cargo-clippy /usr/local/bin/cargo-clippy && \
    ln -s /opt/cargo/bin/clippy-driver /usr/local/bin/clippy-driver
COPY --from=runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=build /published /opt/cpr-worker
RUN ln -s /opt/cpr-worker/CPR.Worker /usr/local/bin/cpr-worker
ENV HOME=/state/home/coder CODEX_HOME=/state/home/coder/.codex CARGO_HOME=/state/cargo
EXPOSE 22
