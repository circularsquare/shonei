#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVER_DIR="$(cd "$SCRIPT_DIR/../shonei-server" && pwd)" || { echo "Could not find shonei-server at $SCRIPT_DIR/../shonei-server"; exit 1; }
cd "$SERVER_DIR" || exit 1
go build -o shonei-market.exe . || { echo "Build failed"; exit 1; }
./shonei-market.exe &
SERVER_PID=$!

echo "Server started (PID: $SERVER_PID)"
trap "kill $SERVER_PID 2>/dev/null" EXIT

echo "Waiting for server to be ready..."
until curl -s --max-time 1 http://localhost:8082/ -o /dev/null 2>/dev/null; do
  # Exit if server process died
  kill -0 $SERVER_PID 2>/dev/null || { echo "Server process died"; exit 1; }
  sleep 0.5
done

cd "$SERVER_DIR/client"
go run main.go
