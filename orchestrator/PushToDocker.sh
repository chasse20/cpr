#!/usr/bin/env bash
set -euo pipefail

tempRegistry="xxx.xxx.xxx.xxx"
tempImage="cpr:latest"
tempDirectory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
tempRemoteImage="$tempRegistry/$tempImage"

docker build --file "$tempDirectory/Dockerfile" --tag "$tempImage" "$tempDirectory"
docker tag "$tempImage" "$tempRemoteImage"
docker push "$tempRemoteImage"
