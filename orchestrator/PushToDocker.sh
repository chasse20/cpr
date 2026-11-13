#!/usr/bin/env bash
set -euo pipefail

tempRegistry="10.0.20.10:5000"
tempImage="cpr:latest"
tempDirectory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
tempRemoteImage="$tempRegistry/$tempImage"

docker build --file "$tempDirectory/Dockerfile" --tag "$tempImage" "$tempDirectory"
docker tag "$tempImage" "$tempRemoteImage"
docker push "$tempRemoteImage"
